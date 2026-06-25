using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoint de auto-registro de cajas: POST /registro-caja/{slug}.
/// Pipeline: buscar tenant → verificar HMAC-SHA256 hex lowercase (X-Caja-Signature)
/// → validar campos → delegar a CajaRegistradaAppService → mapear Result a HTTP.
/// </summary>
public static class RegistroCajaEndpoints
{
    /// <summary>Nombre de la política de rate limiting para este endpoint.</summary>
    public const string RateLimiterPolicy = "registro-caja-rl";

    /// <summary>Header que transporta la firma HMAC del request de registro.</summary>
    public const string SignatureHeader = "X-Caja-Signature";

    /// <summary>
    /// Tamaño máximo del body aceptado (16 KB).
    /// El payload de registro legítimo ocupa ~100 bytes; este límite es amplio pero finito.
    /// </summary>
    private const int LimiteBodyBytes = 16 * 1024;

    public static void MapRegistroCajaEndpoints(this WebApplication app)
    {
        app.MapPost("/registro-caja/{slug}", HandleRegistro)
            .WithName("RegistroCaja")
            .WithSummary("Auto-registro de caja: verifica HMAC y persiste/actualiza la caja en el padrón")
            .RequireRateLimiting(RateLimiterPolicy)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> HandleRegistro(
        string slug,
        HttpRequest request,
        ITenantCacheService tenantCache,
        CajaRegistradaAppService cajaService,
        SystemLogService systemLog,
        ILogger<Program> logger)
    {
        var sw = Stopwatch.StartNew();
        var sourceIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();

        // 1. Buscar tenant por slug
        var tenant = await tenantCache.GetBySlugAsync(slug);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning(
                "Registro de caja rechazado: tenant '{Slug}' no encontrado o inactivo", slug);
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: tenant '{slug}' no encontrado o inactivo",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.NotFound(new { error = $"Tenant '{slug}' no encontrado." });
        }

        // 2. Leer body crudo con límite de tamaño (defensa anti-abuso)
        // Se rechaza antes de bufferizar si Content-Length ya excede el límite,
        // y además se acota la lectura real para no confiar solo en ese header.
        if (request.ContentLength.HasValue && request.ContentLength.Value > LimiteBodyBytes)
        {
            logger.LogWarning(
                "Registro de caja rechazado: body demasiado grande ({Bytes} bytes) para tenant '{Slug}'",
                request.ContentLength.Value, slug);
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: body demasiado grande (Content-Length) para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.BadRequest(new { error = "El cuerpo del request supera el tamaño máximo permitido." });
        }

        using var ms = new MemoryStream();
        var buffer = new byte[LimiteBodyBytes + 1];
        int totalLeidos = 0;
        int leidos;
        while ((leidos = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            totalLeidos += leidos;
            if (totalLeidos > LimiteBodyBytes)
            {
                logger.LogWarning(
                    "Registro de caja rechazado: body real supera {Limite} bytes para tenant '{Slug}'",
                    LimiteBodyBytes, slug);
                systemLog.Warn(SystemLogCategory.Registro,
                    $"Registro rechazado: body supera {LimiteBodyBytes} bytes para tenant '{slug}'",
                    tenantSlug: slug,
                    details: $"sourceIp={sourceIp}");
                return Results.BadRequest(new { error = "El cuerpo del request supera el tamaño máximo permitido." });
            }
            ms.Write(buffer, 0, leidos);
        }
        var body = ms.ToArray();

        // 3. Verificar header de firma
        var signatureHeader = request.Headers[SignatureHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(signatureHeader))
        {
            logger.LogWarning(
                "Registro de caja rechazado: header {Header} ausente para tenant '{Slug}'",
                SignatureHeader, slug);
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: header {SignatureHeader} ausente para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.Unauthorized();
        }

        // 4. Validar HMAC-SHA256 hex lowercase (timing-safe)
        if (!ValidarHmac(tenant.WebhookSecret, body, signatureHeader))
        {
            logger.LogWarning(
                "Registro de caja rechazado: firma inválida para tenant '{Slug}'", slug);
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: firma inválida para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.Unauthorized();
        }

        // 5. Deserializar body
        RegistroCajaRequest? registroReq;
        try
        {
            registroReq = System.Text.Json.JsonSerializer.Deserialize<RegistroCajaRequest>(
                body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: body no es JSON válido para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.BadRequest(new { error = "El cuerpo no es JSON válido." });
        }

        if (registroReq is null)
        {
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: body vacío para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.BadRequest(new { error = "El cuerpo no puede estar vacío." });
        }

        if (string.IsNullOrWhiteSpace(registroReq.Identificador))
        {
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: campo 'identificador' ausente para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.BadRequest(new { error = "El campo 'identificador' es obligatorio." });
        }

        if (string.IsNullOrWhiteSpace(registroReq.CallbackUrl))
        {
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado: campo 'callbackUrl' ausente para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return Results.BadRequest(new { error = "El campo 'callbackUrl' es obligatorio." });
        }

        // 6. Delegar al AppService (valida anti-SSRF, upsert, invalida cache)
        var result = await cajaService.RegistrarAsync(
            tenant.Id,
            registroReq.Identificador,
            registroReq.CallbackUrl);

        if (!result.IsSuccess)
        {
            systemLog.Warn(SystemLogCategory.Registro,
                $"Registro rechazado por validación: {result.Message}",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}");
            return result.Error switch
            {
                ResultError.Validation => Results.BadRequest(new { error = result.Message }),
                ResultError.NotFound   => Results.NotFound(new { error = result.Message }),
                _                     => Results.BadRequest(new { error = result.Message })
            };
        }

        logger.LogInformation(
            "Caja '{Identificador}' registrada para tenant '{Slug}'.",
            registroReq.Identificador, slug);
        systemLog.Info(SystemLogCategory.Registro,
            $"Caja '{registroReq.Identificador}' registrada/actualizada para tenant '{slug}'",
            tenantSlug: slug,
            url: registroReq.CallbackUrl,
            durationMs: sw.ElapsedMilliseconds,
            details: $"identificador={registroReq.Identificador}; sourceIp={sourceIp}");

        return Results.Ok(new
        {
            identificador = result.Value!.Identificador,
            callbackUrl = result.Value.CallbackUrl
        });
    }

    // ─── Helpers privados ─────────────────────────────────────────────────────

    /// <summary>
    /// Valida el HMAC-SHA256 del body con el secret del tenant.
    /// El header debe contener el hex lowercase del hash (timing-safe).
    /// </summary>
    private static bool ValidarHmac(string secret, byte[] body, string signatureHex)
    {
        if (string.IsNullOrEmpty(secret) || body.Length == 0)
            return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var computedHash = HMACSHA256.HashData(secretBytes, body);
        var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        var expectedBytes = Encoding.UTF8.GetBytes(computedHex);
        var actualBytes   = Encoding.UTF8.GetBytes(signatureHex.ToLowerInvariant());

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

/// <summary>Forma del body de registro de caja.</summary>
internal sealed record RegistroCajaRequest(string? Identificador, string? CallbackUrl);
