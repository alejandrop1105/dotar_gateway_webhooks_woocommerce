using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.AspNetCore.Http;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoints de Minimal API para la ingesta de webhooks.
/// Pipeline: lookup tenant → validate HMAC → enqueue → 202 Accepted.
/// </summary>
public static class IngestEndpoints
{
    /// <summary>
    /// Slug reservado para diagnóstico de conectividad end-to-end (externo → Gateway).
    /// El SlugRegex de tenants rechaza underscores, así que no puede colisionar con uno real.
    /// </summary>
    public const string PingSlug = "__ping";

    public static void MapIngestEndpoints(this WebApplication app)
    {
        // Endpoint reservado: tiene prioridad de ruta sobre /ingest/{slug} por ser literal.
        // Acepta GET y POST para que sirva tanto desde un browser/curl como desde un cliente
        // que simule un webhook real. No valida firma, no busca tenant, no encola.
        app.MapMethods($"/ingest/{PingSlug}", new[] { "GET", "POST" }, HandlePing)
            .WithName("IngestPing")
            .WithSummary("Diagnóstico de conectividad: confirma que el Gateway recibe llamadas externas")
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/ingest/{slug}", HandleIngest)
            .WithName("IngestWebhook")
            .WithSummary("Recibe webhook entrante para un tenant específico")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet("/health", (DeployHistoryService deploy) =>
            {
                deploy.Load();
                return Results.Ok(new { status = "healthy", version = deploy.CurrentVersion, timestamp = DateTime.UtcNow });
            })
            .WithName("HealthCheck");
    }

    private static IResult HandlePing(
        HttpRequest request,
        SystemLogService systemLog,
        ILogger<Program> logger)
    {
        var sourceIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = request.Headers["User-Agent"].ToString();
        var method = request.Method;

        logger.LogInformation("Ping de diagnóstico recibido (slug reservado {Slug}) desde {SourceIp}", PingSlug, sourceIp);
        systemLog.Info(SystemLogCategory.Ingest,
            $"Ping de diagnóstico recibido (slug reservado '{PingSlug}')",
            tenantSlug: PingSlug,
            details: $"method={method}; sourceIp={sourceIp}; userAgent={userAgent}");

        return Results.Ok(new
        {
            status = "ok",
            slug = PingSlug,
            method,
            timestamp = DateTime.UtcNow
        });
    }

    private static async Task<IResult> HandleIngest(
        string slug,
        HttpRequest request,
        ITenantCacheService tenantCache,
        HmacSignatureValidator validator,
        RedisQueueService queue,
        SystemLogService systemLog,
        ILogger<Program> logger)
    {
        var sourceIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgentRaw = request.Headers["User-Agent"].ToString();

        // 1. Buscar tenant por slug (desde caché)
        var tenant = await tenantCache.GetBySlugAsync(slug);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Webhook rechazado: tenant '{Slug}' no encontrado o inactivo", slug);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"Webhook rechazado: tenant '{slug}' no encontrado o inactivo",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}; userAgent={userAgentRaw}");
            return Results.Unauthorized();
        }

        // 2. Leer body como bytes
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        if (body.Length == 0)
        {
            logger.LogWarning("Webhook rechazado: body vacío para tenant '{Slug}'", slug);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"Webhook rechazado: body vacío para tenant '{slug}'",
                tenantSlug: slug,
                details: $"sourceIp={sourceIp}; userAgent={userAgentRaw}");
            return Results.Unauthorized();
        }

        // 3. Validar firma HMAC-SHA256 según el esquema del tenant
        if (tenant.SignatureScheme == SignatureScheme.None)
        {
            logger.LogWarning("Tenant '{Slug}' no tiene validación de firma habilitada", slug);
            systemLog.Info(SystemLogCategory.Ingest,
                $"Tenant '{slug}' acepta sin firma (SignatureScheme=None)",
                tenantSlug: slug);
        }
        else if (string.IsNullOrEmpty(tenant.WebhookSecret))
        {
            logger.LogInformation("Tenant '{Slug}' no tiene secret configurado, saltando validación HMAC", slug);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"Tenant '{slug}' sin secret configurado: se omite validación HMAC",
                tenantSlug: slug);
        }
        else
        {
            var headerName = HmacSignatureValidator.ResolveHeader(tenant.SignatureScheme, tenant.SignatureHeader);
            var signature = request.Headers[headerName].FirstOrDefault();
            if (string.IsNullOrEmpty(signature) ||
                !validator.Validate(tenant.SignatureScheme, tenant.WebhookSecret, body, signature))
            {
                logger.LogWarning("Webhook rechazado: firma inválida para tenant '{Slug}' (esquema {Scheme}, header {Header})",
                    slug, tenant.SignatureScheme, headerName);
                systemLog.Warn(SystemLogCategory.Auth,
                    $"Firma HMAC inválida para tenant '{slug}'",
                    tenantSlug: slug,
                    details: $"scheme={tenant.SignatureScheme}; header={headerName}; signaturePresent={!string.IsNullOrEmpty(signature)}; sourceIp={sourceIp}");
                return Results.Unauthorized();
            }
        }

        // 4. Capturar headers del provider para reenviar verbatim al downstream.
        var userAgent = userAgentRaw;
        var forwardedHeaders = HeaderForwardingPolicy.SelectForwardable(
            request.Headers.Select(h => new KeyValuePair<string, string[]>(
                h.Key,
                h.Value.Where(v => v is not null).Select(v => v!).ToArray())),
            originalUserAgent: userAgent);

        // 5. Encolar en Redis
        var payload = System.Text.Encoding.UTF8.GetString(body);
        var sourceUrl = request.Headers["X-WC-Webhook-Source"].FirstOrDefault();
        var eventId = Guid.NewGuid();

        // Setear ProveedorNombre si el tenant tiene ruteo por proveedor activo.
        // Null para el flujo 1-a-1 clásico (sin cambio de comportamiento).
        var proveedorNombre = tenant.RuteoProveedorActivo ? tenant.ProveedorRuteoNombre : null;

        await queue.EnqueueAsync(new QueuedWebhook
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            TargetUrl = tenant.TargetUrl,
            SourceUrl = sourceUrl,
            Payload = payload,
            ReceivedAt = DateTime.UtcNow,
            ForwardedHeaders = forwardedHeaders,
            EventId = eventId,
            ProveedorNombre = proveedorNombre
        });

        logger.LogInformation("Webhook aceptado para tenant '{Slug}' → encolado", slug);

        var topic = request.Headers["X-WC-Webhook-Topic"].FirstOrDefault()
                    ?? request.Headers["X-GitHub-Event"].FirstOrDefault();
        var details = $"bodyBytes={body.Length}; targetUrl={tenant.TargetUrl}; topic={topic}; sourceUrl={sourceUrl}; headers={forwardedHeaders.Count}";

        systemLog.Info(SystemLogCategory.Ingest,
            $"Webhook aceptado para '{slug}' → encolado",
            tenantSlug: slug,
            eventId: eventId,
            url: tenant.TargetUrl,
            details: details);

        return Results.Accepted();
    }
}
