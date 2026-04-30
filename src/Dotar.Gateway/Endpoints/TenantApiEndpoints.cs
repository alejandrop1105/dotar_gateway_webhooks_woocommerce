using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    private static readonly Regex SlugRegex = new("^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$|^[a-z0-9]$", RegexOptions.Compiled);

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

    /// <summary>POST /api/tenants — crea un tenant nuevo. Devuelve el secret generado si no se proveyó.</summary>
    private static async Task<IResult> CreateTenant(
        CreateTenantRequest request,
        IServiceScopeFactory scopeFactory,
        TenantCacheService tenantCache,
        ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "El campo 'name' es obligatorio." });

        if (string.IsNullOrWhiteSpace(request.Slug))
            return Results.BadRequest(new { error = "El campo 'slug' es obligatorio." });

        var slug = request.Slug.Trim().ToLowerInvariant();
        if (!SlugRegex.IsMatch(slug))
            return Results.BadRequest(new { error = "El 'slug' debe ser lowercase alfanumérico con guiones, 1–100 chars, sin guión inicial o final." });

        if (string.IsNullOrWhiteSpace(request.TargetUrl))
            return Results.BadRequest(new { error = "El campo 'targetUrl' es obligatorio." });

        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Results.BadRequest(new { error = "La 'targetUrl' debe ser http:// o https://." });

        var scheme = request.SignatureScheme ?? SignatureScheme.WooCommerce;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        if (await db.Tenants.AnyAsync(t => t.Slug == slug))
            return Results.Conflict(new { error = $"El slug '{slug}' ya está en uso." });

        if (request.RetryPolicyId.HasValue && !await db.RetryPolicies.AnyAsync(p => p.Id == request.RetryPolicyId.Value))
            return Results.BadRequest(new { error = $"La política de reintento {request.RetryPolicyId} no existe." });

        if (request.TenantGroupId.HasValue && !await db.TenantGroups.AnyAsync(g => g.Id == request.TenantGroupId.Value))
            return Results.BadRequest(new { error = $"El grupo {request.TenantGroupId} no existe." });

        var secret = !string.IsNullOrWhiteSpace(request.WebhookSecret)
            ? request.WebhookSecret!.Trim()
            : GenerateSecret();

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            Slug = slug,
            WebhookSecret = secret,
            TargetUrl = request.TargetUrl.Trim(),
            IsActive = request.IsActive ?? true,
            SignatureScheme = scheme,
            SignatureHeader = string.IsNullOrWhiteSpace(request.SignatureHeader) ? null : request.SignatureHeader!.Trim(),
            RetryPolicyId = request.RetryPolicyId,
            TenantGroupId = request.TenantGroupId,
            CreatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        tenantCache.Invalidate(slug);

        logger.LogInformation("Tenant '{Slug}' creado vía API (esquema {Scheme}).", slug, scheme);

        return Results.Created($"/api/tenants/{tenant.Slug}", new
        {
            slug = tenant.Slug,
            name = tenant.Name,
            targetUrl = tenant.TargetUrl,
            webhookSecret = tenant.WebhookSecret,
            signatureScheme = tenant.SignatureScheme.ToString(),
            signatureHeader = tenant.SignatureHeader,
            isActive = tenant.IsActive,
            retryPolicyId = tenant.RetryPolicyId,
            tenantGroupId = tenant.TenantGroupId,
            createdAt = tenant.CreatedAt
        });
    }

    /// <summary>PUT /api/tenants/{slug}/target-url — actualiza la URL destino.</summary>
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

        var normalized = slug.ToLowerInvariant();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        var previousUrl = tenant.TargetUrl;
        tenant.TargetUrl = request.TargetUrl.Trim();
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        tenantCache.Invalidate(normalized);

        logger.LogInformation(
            "URL de destino actualizada para tenant '{Slug}': {OldUrl} → {NewUrl}",
            normalized, previousUrl, tenant.TargetUrl);

        return Results.Ok(new
        {
            slug = tenant.Slug,
            name = tenant.Name,
            targetUrl = tenant.TargetUrl,
            previousUrl,
            updatedAt = tenant.UpdatedAt
        });
    }

    /// <summary>GET /api/tenants/{slug}</summary>
    private static async Task<IResult> GetTenantBySlug(
        string slug,
        IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var normalized = slug.ToLowerInvariant();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        return Results.Ok(new
        {
            slug = tenant.Slug,
            name = tenant.Name,
            targetUrl = tenant.TargetUrl,
            signatureScheme = tenant.SignatureScheme.ToString(),
            signatureHeader = tenant.SignatureHeader,
            isActive = tenant.IsActive,
            createdAt = tenant.CreatedAt,
            updatedAt = tenant.UpdatedAt
        });
    }

    /// <summary>
    /// DELETE /api/tenants/{slug} — borrado físico. Cascadea DeliveryLogs y DeliveryAttempts
    /// (configurado en GatewayDbContext con DeleteBehavior.Cascade).
    /// </summary>
    private static async Task<IResult> DeleteTenant(
        string slug,
        IServiceScopeFactory scopeFactory,
        TenantCacheService tenantCache,
        ILogger<Program> logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var normalized = slug.ToLowerInvariant();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Results.NotFound(new { error = $"Tenant con slug '{slug}' no encontrado." });

        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync();

        tenantCache.Invalidate(normalized);

        logger.LogInformation("Tenant '{Slug}' eliminado vía API.", normalized);
        return Results.NoContent();
    }

    private static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>DTO para crear un tenant vía API.</summary>
public record CreateTenantRequest(
    string Name,
    string Slug,
    string TargetUrl,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null);

/// <summary>DTO para actualizar la URL de destino.</summary>
public record UpdateTargetUrlRequest(string TargetUrl);
