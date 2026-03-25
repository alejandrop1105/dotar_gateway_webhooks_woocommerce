using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoints de API para gestión de tenants desde sistemas externos.
/// </summary>
public static class TenantApiEndpoints
{
    public static void MapTenantApiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants")
            .WithTags("Tenants API");

        group.MapPut("/{slug}/target-url", UpdateTargetUrl)
            .WithName("UpdateTenantTargetUrl")
            .WithSummary("Actualiza la URL de destino de un tenant por su slug")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/{slug}", GetTenantBySlug)
            .WithName("GetTenantBySlug")
            .WithSummary("Obtiene información básica de un tenant por su slug")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// PUT /api/tenants/{slug}/target-url
    /// Body: { "targetUrl": "https://nuevo-destino.com/api/webhooks" }
    /// </summary>
    private static async Task<IResult> UpdateTargetUrl(
        string slug,
        UpdateTargetUrlRequest request,
        IServiceScopeFactory scopeFactory,
        TenantCacheService tenantCache,
        ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.TargetUrl))
            return Results.BadRequest(new { error = "El campo 'targetUrl' es obligatorio." });

        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Results.BadRequest(new { error = "La URL proporcionada no es válida. Debe ser http:// o https://." });

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant());
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        var previousUrl = tenant.TargetUrl;
        tenant.TargetUrl = request.TargetUrl.Trim();
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Invalidar caché del tenant para que use la nueva URL
        tenantCache.Invalidate(slug);

        logger.LogInformation(
            "URL de destino actualizada para tenant '{Slug}': {OldUrl} → {NewUrl}",
            slug, previousUrl, tenant.TargetUrl);

        return Results.Ok(new
        {
            slug = tenant.Slug,
            name = tenant.Name,
            targetUrl = tenant.TargetUrl,
            previousUrl,
            updatedAt = tenant.UpdatedAt
        });
    }

    /// <summary>
    /// GET /api/tenants/{slug}
    /// </summary>
    private static async Task<IResult> GetTenantBySlug(
        string slug,
        IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant());
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        return Results.Ok(new
        {
            slug = tenant.Slug,
            name = tenant.Name,
            targetUrl = tenant.TargetUrl,
            isActive = tenant.IsActive,
            createdAt = tenant.CreatedAt,
            updatedAt = tenant.UpdatedAt
        });
    }
}

/// <summary>DTO para actualizar la URL de destino.</summary>
public record UpdateTargetUrlRequest(string TargetUrl);
