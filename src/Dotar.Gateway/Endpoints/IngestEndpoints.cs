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
    public static void MapIngestEndpoints(this WebApplication app)
    {
        app.MapPost("/ingest/{slug}", HandleIngest)
            .WithName("IngestWebhook")
            .WithSummary("Recibe webhook entrante para un tenant específico")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
            .WithName("HealthCheck");
    }

    private static async Task<IResult> HandleIngest(
        string slug,
        HttpRequest request,
        TenantCacheService tenantCache,
        HmacSignatureValidator validator,
        RedisQueueService queue,
        ILogger<Program> logger)
    {
        // 1. Buscar tenant por slug (desde caché)
        var tenant = await tenantCache.GetBySlugAsync(slug);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Webhook rechazado: tenant '{Slug}' no encontrado o inactivo", slug);
            return Results.Unauthorized();
        }

        // 2. Leer body como bytes
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        if (body.Length == 0)
        {
            logger.LogWarning("Webhook rechazado: body vacío para tenant '{Slug}'", slug);
            return Results.Unauthorized();
        }

        // 3. Validar firma HMAC-SHA256 según el esquema del tenant
        if (tenant.SignatureScheme == SignatureScheme.None)
        {
            logger.LogWarning("Tenant '{Slug}' no tiene validación de firma habilitada", slug);
        }
        else if (string.IsNullOrEmpty(tenant.WebhookSecret))
        {
            logger.LogInformation("Tenant '{Slug}' no tiene secret configurado, saltando validación HMAC", slug);
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
                return Results.Unauthorized();
            }
        }

        // 4. Capturar headers del provider para reenviar verbatim al downstream.
        //    Por qué: WooCommerce/MercadoPago/VTEX/etc. envían X-* específicos (Topic, Event,
        //    Signature, Delivery-ID, ...) que el downstream necesita para decidir acción,
        //    validar HMAC y deduplicar reintentos. Sin esto, el receptor pierde contexto crítico.
        var userAgent = request.Headers["User-Agent"].ToString();
        var forwardedHeaders = HeaderForwardingPolicy.SelectForwardable(
            request.Headers.Select(h => new KeyValuePair<string, string[]>(
                h.Key,
                h.Value.Where(v => v is not null).Select(v => v!).ToArray())),
            originalUserAgent: userAgent);

        // 5. Encolar en Redis
        var payload = System.Text.Encoding.UTF8.GetString(body);
        var sourceUrl = request.Headers["X-WC-Webhook-Source"].FirstOrDefault();
        await queue.EnqueueAsync(new QueuedWebhook
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            TargetUrl = tenant.TargetUrl,
            SourceUrl = sourceUrl,
            Payload = payload,
            ReceivedAt = DateTime.UtcNow,
            ForwardedHeaders = forwardedHeaders
        });

        logger.LogInformation("Webhook aceptado para tenant '{Slug}' → encolado", slug);
        return Results.Accepted();
    }
}
