using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoints de API para gestión de tenants desde sistemas externos.
/// Protegidos por API Key estática (header X-Gateway-Api-Key).
/// </summary>
public static class TenantApiEndpoints
{

    public static void MapTenantApiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants")
            .WithTags("Tenants API")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        group.MapPost("/", CreateTenant)
            .WithName("CreateTenant")
            .WithSummary("Crea un nuevo tenant")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{slug}", UpdateTenant)
            .WithName("UpdateTenant")
            .WithSummary("Actualiza la configuración de un tenant (parcial; sólo los campos provistos)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{slug}/target-url", UpdateTargetUrl)
            .WithName("UpdateTenantTargetUrl")
            .WithSummary("Actualiza la URL de destino de un tenant por su slug")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{slug}", GetTenantBySlug)
            .WithName("GetTenantBySlug")
            .WithSummary("Obtiene información básica de un tenant por su slug")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{slug}", DeleteTenant)
            .WithName("DeleteTenant")
            .WithSummary("Elimina un tenant y su histórico de entregas (cascada)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>POST /api/tenants — crea un tenant nuevo. Delega en TenantAppService.</summary>
    private static async Task<IResult> CreateTenant(
        CreateTenantRequest request,
        TenantAppService appService,
        ILogger<Program> logger)
    {
        var input = new CreateTenantInput(
            Name:                  request.Name,
            Slug:                  request.Slug,
            TargetUrl:             request.TargetUrl,
            WebhookSecret:         request.WebhookSecret,
            SignatureScheme:       request.SignatureScheme,
            SignatureHeader:       request.SignatureHeader,
            IsActive:              request.IsActive,
            RetryPolicyId:         request.RetryPolicyId,
            TenantGroupId:         request.TenantGroupId,
            RuteoProveedorActivo:  request.RuteoProveedorActivo,
            ProveedorRuteoNombre:  request.ProveedorRuteoNombre,
            SucursalMetaKey:       request.SucursalMetaKey,
            SucursalMetaSeparador: request.SucursalMetaSeparador);

        var result = await appService.CreateAsync(input);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                ResultError.Conflict  => Results.Conflict(new { error = result.Message }),
                _                     => Results.BadRequest(new { error = result.Message })
            };
        }

        var tenant = result.Value!;
        logger.LogInformation("Tenant '{Slug}' creado vía API.", tenant.Slug);

        return Results.Created($"/api/tenants/{tenant.Slug}", new
        {
            slug                  = tenant.Slug,
            name                  = tenant.Name,
            targetUrl             = tenant.TargetUrl,
            webhookSecret         = tenant.WebhookSecret,
            signatureScheme       = tenant.SignatureScheme.ToString(),
            signatureHeader       = tenant.SignatureHeader,
            isActive              = tenant.IsActive,
            retryPolicyId         = tenant.RetryPolicyId,
            tenantGroupId         = tenant.TenantGroupId,
            createdAt             = tenant.CreatedAt,
            ruteoProveedorActivo  = tenant.RuteoProveedorActivo,
            proveedorRuteoNombre  = tenant.ProveedorRuteoNombre,
            sucursalMetaKey       = tenant.SucursalMetaKey,
            sucursalMetaSeparador = tenant.SucursalMetaSeparador
        });
    }

    /// <summary>
    /// PUT /api/tenants/{slug} — actualización parcial. Sólo se modifican los campos provistos
    /// (no null). Convenciones para "limpiar" campos opcionales:
    ///   - signatureHeader: enviar "" (string vacío) para volver al header default del esquema.
    ///   - retryPolicyId / tenantGroupId: enviar 0 para desasociar.
    /// Delega en TenantAppService.UpdateAsync.
    /// </summary>
    private static async Task<IResult> UpdateTenant(
        string slug,
        UpdateTenantRequest request,
        TenantAppService appService,
        ILogger<Program> logger)
    {
        var input = new UpdateTenantInput(
            Name:                  request.Name,
            TargetUrl:             request.TargetUrl,
            WebhookSecret:         request.WebhookSecret,
            SignatureScheme:       request.SignatureScheme,
            SignatureHeader:       request.SignatureHeader,
            IsActive:              request.IsActive,
            RetryPolicyId:         request.RetryPolicyId,
            TenantGroupId:         request.TenantGroupId,
            RuteoProveedorActivo:  request.RuteoProveedorActivo,
            ProveedorRuteoNombre:  request.ProveedorRuteoNombre,
            SucursalMetaKey:       request.SucursalMetaKey,
            SucursalMetaSeparador: request.SucursalMetaSeparador);

        var result = await appService.UpdateAsync(slug, input);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                ResultError.NotFound  => Results.NotFound(new { error = result.Message }),
                _                     => Results.BadRequest(new { error = result.Message })
            };
        }

        var tenant = result.Value!;
        logger.LogInformation("Tenant '{Slug}' actualizado vía API.", tenant.Slug);

        return Results.Ok(new
        {
            slug                  = tenant.Slug,
            name                  = tenant.Name,
            targetUrl             = tenant.TargetUrl,
            signatureScheme       = tenant.SignatureScheme.ToString(),
            signatureHeader       = tenant.SignatureHeader,
            isActive              = tenant.IsActive,
            retryPolicyId         = tenant.RetryPolicyId,
            tenantGroupId         = tenant.TenantGroupId,
            createdAt             = tenant.CreatedAt,
            updatedAt             = tenant.UpdatedAt,
            ruteoProveedorActivo  = tenant.RuteoProveedorActivo,
            proveedorRuteoNombre  = tenant.ProveedorRuteoNombre,
            sucursalMetaKey       = tenant.SucursalMetaKey,
            sucursalMetaSeparador = tenant.SucursalMetaSeparador
        });
    }

    /// <summary>
    /// PUT /api/tenants/{slug}/target-url — actualiza la URL destino.
    /// Delega en TenantAppService.UpdateTargetUrlAsync.
    /// </summary>
    private static async Task<IResult> UpdateTargetUrl(
        string slug,
        UpdateTargetUrlRequest request,
        TenantAppService appService,
        ILogger<Program> logger)
    {
        var result = await appService.UpdateTargetUrlAsync(slug, request.TargetUrl);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                ResultError.NotFound  => Results.NotFound(new { error = result.Message }),
                _                     => Results.BadRequest(new { error = result.Message })
            };
        }

        var tenant      = result.Value!.Tenant;
        var previousUrl = result.Value!.PreviousUrl;

        logger.LogInformation(
            "URL de destino actualizada para tenant '{Slug}': {OldUrl} → {NewUrl}",
            tenant.Slug, previousUrl, tenant.TargetUrl);

        return Results.Ok(new
        {
            slug        = tenant.Slug,
            name        = tenant.Name,
            targetUrl   = tenant.TargetUrl,
            previousUrl,
            updatedAt   = tenant.UpdatedAt
        });
    }

    /// <summary>GET /api/tenants/{slug}</summary>
    private static async Task<IResult> GetTenantBySlug(
        string slug,
        GatewayDbContext db)
    {
        var normalized = slug.ToLowerInvariant();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        return Results.Ok(new
        {
            slug                  = tenant.Slug,
            name                  = tenant.Name,
            targetUrl             = tenant.TargetUrl,
            signatureScheme       = tenant.SignatureScheme.ToString(),
            signatureHeader       = tenant.SignatureHeader,
            isActive              = tenant.IsActive,
            createdAt             = tenant.CreatedAt,
            updatedAt             = tenant.UpdatedAt,
            ruteoProveedorActivo  = tenant.RuteoProveedorActivo,
            proveedorRuteoNombre  = tenant.ProveedorRuteoNombre,
            sucursalMetaKey       = tenant.SucursalMetaKey,
            sucursalMetaSeparador = tenant.SucursalMetaSeparador
        });
    }

    /// <summary>
    /// DELETE /api/tenants/{slug} — borrado físico. Cascadea DeliveryLogs y DeliveryAttempts
    /// (configurado en GatewayDbContext con DeleteBehavior.Cascade).
    /// Delega en TenantAppService.DeleteAsync.
    /// </summary>
    private static async Task<IResult> DeleteTenant(
        string slug,
        TenantAppService appService,
        ILogger<Program> logger)
    {
        var result = await appService.DeleteAsync(slug);

        if (!result.IsSuccess)
            return result.Error switch
            {
                ResultError.NotFound => Results.NotFound(new { error = result.Message }),
                _                    => Results.BadRequest(new { error = result.Message })
            };

        logger.LogInformation("Tenant '{Slug}' eliminado vía API.", slug.ToLowerInvariant());
        return Results.NoContent();
    }

}

/// <summary>
/// DTO para crear un tenant vía API.
/// Los 4 campos de ruteo son opcionales al final para mantener compat con el ERP.
/// </summary>
public record CreateTenantRequest(
    string Name,
    string Slug,
    string TargetUrl,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null,
    bool? RuteoProveedorActivo = null,
    string? ProveedorRuteoNombre = null,
    string? SucursalMetaKey = null,
    string? SucursalMetaSeparador = null);

/// <summary>
/// DTO para actualización parcial de un tenant. Todos los campos son opcionales:
/// los que lleguen null no se modifican. Ver convenciones de limpieza en UpdateTenant.
/// Los 4 campos de ruteo son opcionales; null = sin cambio.
/// RuteoProveedorActivo=false limpia los 3 dependientes (semántica de apagado).
/// </summary>
public record UpdateTenantRequest(
    string? Name = null,
    string? TargetUrl = null,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null,
    bool? RuteoProveedorActivo = null,
    string? ProveedorRuteoNombre = null,
    string? SucursalMetaKey = null,
    string? SucursalMetaSeparador = null);

/// <summary>DTO para actualizar la URL de destino.</summary>
public record UpdateTargetUrlRequest(string TargetUrl);
