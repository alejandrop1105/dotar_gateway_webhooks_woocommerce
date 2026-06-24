using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotar.Gateway.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Dotar.Gateway.Providers;

/// <summary>
/// Implementación de IWebhookProvider para MercadoPago.
/// Registrada con keyed DI como "mercadopago".
///
/// Lógica de firma x-signature (fuente: docs oficiales MP):
///   Header: x-signature: ts={timestamp_ms},v1={hmac_hex}
///   Manifest a firmar: "id:{data.id};request-id:{x-request-id};ts:{ts};"
///   Se omiten los campos ausentes.
///   HMAC-SHA256 en hex con el signingSecret como key.
/// </summary>
public class MercadoPagoProvider : IWebhookProvider
{
    public string Nombre => "mercadopago";

    private readonly HttpClient _httpClient;
    private readonly ILogger<MercadoPagoProvider> _logger;

    public MercadoPagoProvider(HttpClient httpClient, ILogger<MercadoPagoProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// MP envía user_id en el body JSON de la notificación entrante.
    public string? ResolverCuentaExterna(IHeaderDictionary headers, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // user_id puede venir como número o como string
            if (root.TryGetProperty("user_id", out var userIdProp))
            {
                return userIdProp.ValueKind == JsonValueKind.String
                    ? userIdProp.GetString()
                    : userIdProp.GetRawText();
            }

            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Body de notificación MP no es JSON válido al resolver CuentaExterna");
            return null;
        }
    }

    /// <inheritdoc/>
    /// Valida header x-signature de MP: ts={ts},v1={hmac_hex}
    /// Manifest: "id:{data.id};request-id:{x-request-id};ts:{ts};" (omitir campos ausentes)
    public bool ValidarFirmaEntrante(IHeaderDictionary headers, byte[] body, ProveedorWebhookConfig config)
    {
        try
        {
            // 1. Extraer x-signature
            var xSig = headers["x-signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(xSig))
                return false;

            // 2. Parsear ts y v1 del header
            string? ts = null;
            string? v1 = null;
            foreach (var parte in xSig.Split(','))
            {
                var kv = parte.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0].Trim() == "ts") ts = kv[1].Trim();
                else if (kv[0].Trim() == "v1") v1 = kv[1].Trim();
            }

            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(v1))
                return false;

            // 3. Extraer data.id del body
            string? dataId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("id", out var idProp))
                {
                    dataId = idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()
                        : idProp.GetRawText();
                }
            }
            catch (JsonException)
            {
                // Si el body no parsea, no hay dataId
            }

            // 4. Construir manifest (omitir campos ausentes)
            var manifest = new StringBuilder();
            if (!string.IsNullOrEmpty(dataId))
                manifest.Append($"id:{dataId};");

            var requestId = headers["x-request-id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestId))
                manifest.Append($"request-id:{requestId};");

            manifest.Append($"ts:{ts};");

            // 5. Extraer signingSecret de CredencialesCifradas
            var signingSecret = ExtraerSigningSecret(config.CredencialesCifradas);
            if (string.IsNullOrEmpty(signingSecret))
                return false;

            // 6. Calcular HMAC y comparar timing-safe
            var keyBytes = Encoding.UTF8.GetBytes(signingSecret);
            var msgBytes = Encoding.UTF8.GetBytes(manifest.ToString());
            var computedBytes = HMACSHA256.HashData(keyBytes, msgBytes);
            var computedHex = Convert.ToHexString(computedBytes).ToLowerInvariant();

            // v1 ya viene en hex; normalizar a lowercase para la comparación
            var v1Lower = v1.ToLowerInvariant();
            if (computedHex.Length != v1Lower.Length)
                return false;

            var computedHexBytes = Encoding.UTF8.GetBytes(computedHex);
            var v1Bytes = Encoding.UTF8.GetBytes(v1Lower);
            return CryptographicOperations.FixedTimeEquals(computedHexBytes, v1Bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error inesperado al validar firma MP x-signature");
            return false;
        }
    }

    /// <inheritdoc/>
    /// GET {BaseUrl}/v1/payments/{idEvento} con Authorization: Bearer {accessToken}
    public async Task<EnrichmentResult> EnriquecerAsync(string idEvento, ProveedorWebhookConfig config, CancellationToken ct)
    {
        try
        {
            var accessToken = ExtraerAccessToken(config.CredencialesCifradas);
            var url = $"{config.BaseUrl.TrimEnd('/')}/v1/payments/{idEvento}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Error al enriquecer pago MP {IdEvento}: HTTP {Status}. Body: {Body}",
                    idEvento, (int)response.StatusCode, errorBody);
                return EnrichmentResult.Fallo($"HTTP {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            return EnrichmentResult.Ok(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción al enriquecer pago MP {IdEvento}", idEvento);
            return EnrichmentResult.Fallo(ex.Message);
        }
    }

    /// <inheritdoc/>
    /// Extrae routing key: external_reference.Split("__", 2)[0].
    /// Sin "__", parte izquierda vacía, o campo ausente → Invalid.
    /// El identificador conserva guiones y guion bajo simple (ej. 003-CAJA_2); el separador es "__" doble.
    public RoutingKeyResult ExtraerRoutingKey(string payloadEnriquecido)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadEnriquecido);
            if (!doc.RootElement.TryGetProperty("external_reference", out var prop))
                return RoutingKeyResult.Invalid;

            if (prop.ValueKind != JsonValueKind.String)
                return RoutingKeyResult.Invalid;

            var externalRef = prop.GetString();
            if (string.IsNullOrEmpty(externalRef))
                return RoutingKeyResult.Invalid;

            var partes = externalRef.Split("__", 2);
            if (partes.Length < 2)
                return RoutingKeyResult.Invalid;

            var identificador = partes[0];
            if (string.IsNullOrEmpty(identificador))
                return RoutingKeyResult.Invalid;

            return RoutingKeyResult.Valido(identificador);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Payload enriquecido MP no es JSON válido al extraer routing key");
            return RoutingKeyResult.Invalid;
        }
    }

    // ─── Helpers privados para leer CredencialesCifradas ───────────────────

    /// <summary>
    /// Lee el AccessToken del JSON de CredencialesCifradas.
    /// En WU-3 este JSON será descifrado por IDataProtector; acá se lee directamente
    /// porque el cifrado se introduce en PR 2.
    /// </summary>
    private static string? ExtraerAccessToken(string credencialesCifradas)
    {
        try
        {
            using var doc = JsonDocument.Parse(credencialesCifradas);
            if (doc.RootElement.TryGetProperty("AccessToken", out var prop))
                return prop.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lee el SigningSecret del JSON de CredencialesCifradas.
    /// En WU-3 este JSON será descifrado por IDataProtector.
    /// </summary>
    private static string? ExtraerSigningSecret(string credencialesCifradas)
    {
        try
        {
            using var doc = JsonDocument.Parse(credencialesCifradas);
            if (doc.RootElement.TryGetProperty("SigningSecret", out var prop))
                return prop.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
