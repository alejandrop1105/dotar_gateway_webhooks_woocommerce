using System.Text;
using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Providers;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoint de ingesta para webhooks de proveedores externos.
/// Pipeline: resolver proveedor por ruta → resolver cuenta externa del payload
/// → buscar ProveedorWebhookConfig (con TenantId + credenciales) → validar firma → encolar → 202.
/// La URL NO incluye slug de tenant: el tenant se resuelve por el payload del proveedor.
/// IngestEndpoints.cs NO se modifica; este endpoint es independiente.
/// </summary>
public static class WebhookProveedorEndpoints
{
    /// <summary>
    /// Tamaño máximo del body aceptado (256 KB).
    /// Los payloads de notificación de proveedores son pequeños (topic + id);
    /// este límite defensivo cubre margen ante variaciones del esquema.
    /// </summary>
    private const int LimiteBodyBytes = 256 * 1024;

    public static void MapWebhookProveedorEndpoints(this WebApplication app)
    {
        app.MapPost("/webhook/{proveedor}", HandleWebhookProveedor)
            .WithName("WebhookProveedor")
            .WithSummary("Recibe webhook de un proveedor externo; resuelve tenant por cuenta externa")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleWebhookProveedor(
        string proveedor,
        HttpRequest request,
        IServiceProvider services,
        ProveedorWebhookConfigAppService configService,
        RedisQueueService queue,
        SystemLogService systemLog,
        ILogger<Program> logger)
    {
        var sourceIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();

        // 1. Resolver IWebhookProvider por la ruta (keyed DI por nombre del proveedor)
        var provider = services.GetKeyedService<IWebhookProvider>(proveedor);
        if (provider is null)
        {
            logger.LogWarning(
                "Webhook rechazado: proveedor '{Proveedor}' no registrado (IP: {Ip})",
                proveedor, sourceIp);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"Proveedor '{proveedor}' no registrado en el gateway",
                details: $"proveedor={proveedor}; sourceIp={sourceIp}");
            return Results.NotFound(new { error = $"Proveedor '{proveedor}' no encontrado." });
        }

        // 2. Leer body crudo con límite de tamaño (defensa anti-abuso)
        if (request.ContentLength.HasValue && request.ContentLength.Value > LimiteBodyBytes)
        {
            logger.LogWarning(
                "Webhook proveedor '{Proveedor}' rechazado: body demasiado grande ({Bytes} bytes)",
                proveedor, request.ContentLength.Value);
            return Results.BadRequest(new { error = "El cuerpo del request supera el tamaño máximo." });
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
                    "Webhook proveedor '{Proveedor}' rechazado: body real supera {Limite} bytes",
                    proveedor, LimiteBodyBytes);
                return Results.BadRequest(new { error = "El cuerpo del request supera el tamaño máximo." });
            }
            ms.Write(buffer, 0, leidos);
        }
        var body = ms.ToArray();

        // 3. Resolver cuenta externa del payload (ej. user_id en MP)
        var cuentaExternaId = provider.ResolverCuentaExterna(request.Headers, body);
        if (string.IsNullOrEmpty(cuentaExternaId))
        {
            logger.LogWarning(
                "Webhook proveedor '{Proveedor}' rechazado: no se pudo resolver cuenta externa",
                proveedor);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"No se pudo resolver cuenta externa del proveedor '{proveedor}'",
                details: $"proveedor={proveedor}; bodyBytes={body.Length}; sourceIp={sourceIp}");
            return Results.NotFound(new { error = "No se pudo identificar la cuenta del proveedor." });
        }

        // 4. Lookup ProveedorWebhookConfig por (ProveedorNombre, CuentaExternaId)
        //    Una sola consulta: retorna TenantId + credenciales descifradas
        var config = await configService.GetCompletoByProveedorYCuentaAsync(proveedor, cuentaExternaId);
        if (config is null)
        {
            logger.LogWarning(
                "Webhook proveedor '{Proveedor}' rechazado: cuenta '{Cuenta}' sin configuración",
                proveedor, cuentaExternaId);
            systemLog.Warn(SystemLogCategory.Ingest,
                $"Cuenta externa '{cuentaExternaId}' sin config para proveedor '{proveedor}'",
                details: $"proveedor={proveedor}; cuentaExternaId={cuentaExternaId}; sourceIp={sourceIp}");
            return Results.NotFound(new { error = "Configuración de proveedor no encontrada para esta cuenta." });
        }

        // Construir entidad de config para ValidarFirmaEntrante.
        // El proveedor necesita leer SigningSecret desde CredencialesCifradas (JSON en claro
        // que incluye los campos que el proveedor conoce, construido desde el DTO descifrado).
        var configEntidad = new Domain.Entities.ProveedorWebhookConfig
        {
            TenantId = config.TenantId,
            ProveedorNombre = proveedor,
            CuentaExternaId = cuentaExternaId,
            CredencialesCifradas = System.Text.Json.JsonSerializer.Serialize(new
            {
                SigningSecret = config.SigningSecret,
                AccessToken = config.AccessToken
            }),
            BaseUrl = config.BaseUrl,
            IsActive = config.IsActive
        };

        // 5. Validar firma entrante (timing-safe, a cargo del proveedor)
        if (!provider.ValidarFirmaEntrante(request.Headers, body, configEntidad))
        {
            logger.LogWarning(
                "Webhook proveedor '{Proveedor}' rechazado: firma inválida para cuenta '{Cuenta}'",
                proveedor, cuentaExternaId);
            systemLog.Warn(SystemLogCategory.Auth,
                $"Firma inválida en webhook del proveedor '{proveedor}' para cuenta '{cuentaExternaId}'",
                details: $"proveedor={proveedor}; cuentaExternaId={cuentaExternaId}; sourceIp={sourceIp}");
            return Results.Unauthorized();
        }

        // 6. Encolar QueuedWebhook con TenantId y ProveedorNombre → 202
        var payload = Encoding.UTF8.GetString(body);
        var eventId = Guid.NewGuid();

        await queue.EnqueueAsync(new QueuedWebhook
        {
            TenantId = config.TenantId,
            TenantSlug = string.Empty, // sin slug: el tenant se resolvió por cuenta externa
            TargetUrl = string.Empty,  // sin target fija: el worker la resuelve por caja registrada
            Payload = payload,
            ProveedorNombre = proveedor,
            EventId = eventId,
            ReceivedAt = DateTime.UtcNow
        });

        logger.LogInformation(
            "Webhook de proveedor '{Proveedor}' para cuenta '{Cuenta}' aceptado → encolado",
            proveedor, cuentaExternaId);

        systemLog.Info(SystemLogCategory.Ingest,
            $"Webhook de proveedor '{proveedor}' para cuenta '{cuentaExternaId}' encolado",
            eventId: eventId,
            details: $"proveedor={proveedor}; cuentaExternaId={cuentaExternaId}; bodyBytes={body.Length}; tenantId={config.TenantId}");

        return Results.Accepted();
    }
}
