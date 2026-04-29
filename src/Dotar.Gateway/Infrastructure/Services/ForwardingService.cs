using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Servicio de reenvío HTTP que entrega los webhooks al destino final del tenant.
/// Reenvía verbatim los headers X-* del provider (filtrados por HeaderForwardingPolicy)
/// para que el downstream conserve contexto: topic/event, firma HMAC, delivery-id, etc.
/// </summary>
public class ForwardingService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<ForwardingService> _logger;

    public ForwardingService(
        IHttpClientFactory clientFactory,
        ILogger<ForwardingService> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reenvía el payload JSON al destino final, propagando los headers del provider.
    /// </summary>
    public async Task<ForwardResult> ForwardAsync(
        string targetUrl,
        string payload,
        string tenantSlug,
        IReadOnlyDictionary<string, string>? forwardedHeaders = null)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var client = _clientFactory.CreateClient("GatewayForwarder");

            // Construimos manualmente HttpRequestMessage para poder llamar TryAddWithoutValidation
            // y preservar el nombre exacto de cada header (algunos providers son sensibles a la
            // capitalización: X-WC-Webhook-Topic ≠ X-Wc-Webhook-Topic en validadores estrictos).
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            // Headers propios del Gateway (NO deben pisar headers del provider que también
            // arranquen con X-Dotar-* — la prioridad la tiene el provider).
            request.Headers.TryAddWithoutValidation("X-Dotar-Gateway-ID", tenantSlug);

            // Aplicar headers del provider (X-* + X-Original-User-Agent) verbatim.
            if (forwardedHeaders is { Count: > 0 })
                ApplyForwardedHeaders(request, forwardedHeaders);

            var response = await client.SendAsync(request);
            sw.Stop();

            _logger.LogInformation(
                "Webhook reenviado a {Url} → {StatusCode} en {Duration}ms ({HeaderCount} headers propagados)",
                targetUrl, (int)response.StatusCode, sw.ElapsedMilliseconds,
                forwardedHeaders?.Count ?? 0);

            return new ForwardResult
            {
                StatusCode = (int)response.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                IsSuccess = response.IsSuccessStatusCode,
                ErrorMessage = response.IsSuccessStatusCode
                    ? null
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Timeout al reenviar a {Url} ({Duration}ms)", targetUrl, sw.ElapsedMilliseconds);
            return new ForwardResult
            {
                StatusCode = (int)HttpStatusCode.GatewayTimeout,
                DurationMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Timeout al conectar con el destino",
                IsSuccess = false
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error al reenviar webhook a {Url}", targetUrl);
            return new ForwardResult
            {
                StatusCode = 0,
                DurationMs = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                IsSuccess = false
            };
        }
    }

    /// <summary>
    /// Aplica los headers al request saliente. La mayoría son request-headers; algunos pocos
    /// son entity-headers (van en Content). Probamos ambos para máxima compatibilidad.
    /// </summary>
    private static void ApplyForwardedHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            // Defensa en profundidad: el Ingest ya filtró, pero por si llegan headers fabricados
            // desde la cola por algún reproceso o test, re-aplicamos la política aquí.
            if (!HeaderForwardingPolicy.ShouldForward(name)
                && !string.Equals(name, HeaderForwardingPolicy.OriginalUserAgentHeader, StringComparison.OrdinalIgnoreCase))
                continue;

            // TryAddWithoutValidation preserva la capitalización original del nombre.
            if (request.Headers.TryAddWithoutValidation(name, value)) continue;

            // Si HttpClient lo rechaza como request-header (caso raro), probamos como content-header.
            request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

public class ForwardResult
{
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSuccess { get; set; }
}
