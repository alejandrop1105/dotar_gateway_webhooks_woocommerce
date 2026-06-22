using System.Text.Json;
using Dotar.Gateway.Application;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoints de administración para la configuración de proveedores de webhook.
/// Grupo: /api/proveedores/config — protegido por API Key (header X-Gateway-Api-Key).
/// POST: upsert de configuración (nuevo → 201, existente → 200 sin duplicado).
/// GET:  listado de configuraciones (NUNCA expone credenciales ni campos sensibles).
/// </summary>
public static class ProveedorConfigApiEndpoints
{
    public static void MapProveedorConfigApiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/proveedores/config")
            .WithTags("Proveedores Config API")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        group.MapPost("/", UpsertConfig)
            .WithName("UpsertProveedorConfig")
            .WithSummary("Crea o actualiza la configuración de un proveedor para un tenant")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", ListarConfigs)
            .WithName("ListarProveedoresConfig")
            .WithSummary("Lista las configuraciones de proveedores (sin credenciales sensibles)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// POST /api/proveedores/config
    /// Upsert de configuración de proveedor para un tenant.
    /// Identifica el tenant por slug (campo "tenant").
    /// Nuevo registro → 201; actualización de existente → 200.
    /// </summary>
    private static async Task<IResult> UpsertConfig(
        ProveedorConfigUpsertRequest request,
        GatewayDbContext db,
        ProveedorWebhookConfigAppService appService,
        ILogger<Program> logger)
    {
        // Resolver tenant por slug
        var slug = request.Tenant?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug))
            return Results.BadRequest(new { error = "El campo 'tenant' (slug) es obligatorio." });

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        // Validaciones básicas de campos obligatorios
        if (string.IsNullOrWhiteSpace(request.ProveedorNombre))
            return Results.BadRequest(new { error = "El campo 'proveedorNombre' es obligatorio." });

        if (string.IsNullOrWhiteSpace(request.CuentaExternaId))
            return Results.BadRequest(new { error = "El campo 'cuentaExternaId' es obligatorio." });

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Results.BadRequest(new { error = "El campo 'accessToken' es obligatorio." });

        if (string.IsNullOrWhiteSpace(request.SigningSecret))
            return Results.BadRequest(new { error = "El campo 'signingSecret' es obligatorio." });

        // Determinar si es inserción nueva (para el código de respuesta)
        var existe = await db.ProveedoresWebhookConfig
            .AnyAsync(p => p.TenantId == tenant.Id && p.ProveedorNombre == request.ProveedorNombre);

        // Construir el JSON de credenciales que el AppService cifrará
        // Usar snake_case para compatibilidad con el CredencialesJson interno del AppService
        var credencialesJson = JsonSerializer.Serialize(new
        {
            access_token = request.AccessToken,
            signing_secret = request.SigningSecret
        });

        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
            ? "https://api.mercadopago.com"
            : request.BaseUrl;

        var result = await appService.UpsertAsync(
            tenantId: tenant.Id,
            proveedorNombre: request.ProveedorNombre,
            cuentaExternaId: request.CuentaExternaId,
            credencialesJson: credencialesJson,
            baseUrl: baseUrl,
            isActive: request.IsActive);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                ResultError.NotFound   => Results.NotFound(new { error = result.Message }),
                ResultError.Validation => Results.BadRequest(new { error = result.Message }),
                _                      => Results.BadRequest(new { error = result.Message })
            };
        }

        logger.LogInformation(
            "Config del proveedor '{Proveedor}' para tenant '{Slug}' {Accion} vía API.",
            request.ProveedorNombre, slug, existe ? "actualizada" : "creada");

        if (!existe)
        {
            return Results.Created($"/api/proveedores/config", new
            {
                tenant = tenant.Slug,
                proveedorNombre = request.ProveedorNombre,
                cuentaExternaId = request.CuentaExternaId,
                baseUrl,
                isActive = request.IsActive
            });
        }

        return Results.Ok(new
        {
            tenant = tenant.Slug,
            proveedorNombre = request.ProveedorNombre,
            cuentaExternaId = request.CuentaExternaId,
            baseUrl,
            isActive = request.IsActive
        });
    }

    /// <summary>
    /// GET /api/proveedores/config
    /// Lista las configuraciones de proveedores.
    /// SEGURIDAD CRÍTICA: la respuesta NUNCA incluye accessToken, signingSecret,
    /// ni el campo CredencialesCifradas en ninguna forma (plano ni cifrado).
    /// Devuelve únicamente metadata no sensible.
    /// </summary>
    private static async Task<IResult> ListarConfigs(
        GatewayDbContext db)
    {
        var configs = await db.ProveedoresWebhookConfig
            .AsNoTracking()
            .Include(p => p.Tenant)
            .OrderBy(p => p.TenantId)
            .ThenBy(p => p.ProveedorNombre)
            .Select(p => new ProveedorConfigMetadataDto(
                p.Tenant.Slug,
                p.ProveedorNombre,
                p.CuentaExternaId,
                p.BaseUrl,
                p.IsActive,
                p.CreatedAt,
                p.UpdatedAt))
            .ToListAsync();

        return Results.Ok(configs);
    }
}

/// <summary>
/// DTO de entrada para el upsert de configuración de proveedor.
/// Las credenciales (accessToken, signingSecret) se reciben aquí pero
/// el AppService las cifra inmediatamente antes de persistirlas.
/// </summary>
public record ProveedorConfigUpsertRequest(
    string? Tenant,
    string? ProveedorNombre,
    string? CuentaExternaId,
    string? AccessToken,
    string? SigningSecret,
    string? BaseUrl = null,
    bool IsActive = true);

/// <summary>
/// DTO de listado de configuración — NO contiene campos sensibles.
/// Solo metadata pública: slug del tenant, proveedor, cuenta, baseUrl, estado y timestamps.
/// </summary>
public record ProveedorConfigMetadataDto(
    string TenantSlug,
    string ProveedorNombre,
    string CuentaExternaId,
    string BaseUrl,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
