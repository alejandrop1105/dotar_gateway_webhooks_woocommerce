using System.Security.Cryptography;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Application;

/// <summary>
/// Datos para crear un tenant.
/// Equivale a CreateTenantRequest pero sin acoplar a la capa HTTP.
/// </summary>
public sealed record CreateTenantInput(
    string Name,
    string Slug,
    string TargetUrl,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null);

/// <summary>
/// Resultado de actualizar la URL de destino de un tenant.
/// Encapsula el tenant actualizado y la URL anterior para que el endpoint
/// pueda incluir <c>previousUrl</c> en la respuesta sin volver a consultar la base de datos.
/// </summary>
public sealed record TargetUrlActualizada(Tenant Tenant, string PreviousUrl);

/// <summary>
/// Datos para actualización parcial de un tenant.
/// Campos null = no se modifican.
/// El slug NO está presente: es inmutable tras la creación (imposible cambiar por construcción del contrato).
/// </summary>
public sealed record UpdateTenantInput(
    string? Name = null,
    string? TargetUrl = null,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null);

/// <summary>
/// Servicio de aplicación para operaciones de Tenant (Scoped).
/// Centraliza toda la lógica de negocio de tenants: validación de slug, normalización,
/// unicidad, validación de FKs, generación de secret, invalidación de caché.
/// Los consumidores (endpoints Minimal API y componentes Blazor) delegan en este servicio.
/// </summary>
public sealed class TenantAppService
{
    private readonly GatewayDbContext _db;
    private readonly ITenantCacheService _cache;
    private readonly ILogger<TenantAppService> _logger;

    public TenantAppService(
        GatewayDbContext db,
        ITenantCacheService cache,
        ILogger<TenantAppService> logger)
        => (_db, _cache, _logger) = (db, cache, logger);

    /// <summary>
    /// Crea un tenant. Valida nombre/slug/url, formato de slug, unicidad y FKs.
    /// Genera secret base64 si corresponde. Invalida caché. Devuelve el Tenant creado.
    /// </summary>
    public async Task<Result<Tenant>> CreateAsync(CreateTenantInput input)
    {
        // Validar campos requeridos
        if (string.IsNullOrWhiteSpace(input.Name))
            return Result<Tenant>.Validation("El campo 'name' es obligatorio.");

        if (string.IsNullOrWhiteSpace(input.Slug))
            return Result<Tenant>.Validation("El campo 'slug' es obligatorio.");

        if (string.IsNullOrWhiteSpace(input.TargetUrl))
            return Result<Tenant>.Validation("El campo 'targetUrl' es obligatorio.");

        // Normalizar y validar slug
        var slug = Tenant.NormalizeSlug(input.Slug);
        if (!Tenant.IsValidSlug(slug))
            return Result<Tenant>.Validation(
                "El 'slug' debe ser lowercase alfanumérico con guiones, 1–100 chars, sin guión inicial o final.");

        // Validar URL
        if (!Uri.TryCreate(input.TargetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Result<Tenant>.Validation("La 'targetUrl' debe ser http:// o https://.");

        // Verificar unicidad de slug
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug))
            return Result<Tenant>.Conflict($"El slug '{slug}' ya está en uso.");

        // Validar FKs
        if (input.RetryPolicyId.HasValue
            && !await _db.RetryPolicies.AnyAsync(p => p.Id == input.RetryPolicyId.Value))
            return Result<Tenant>.Validation($"La política de reintento {input.RetryPolicyId} no existe.");

        if (input.TenantGroupId.HasValue
            && !await _db.TenantGroups.AnyAsync(g => g.Id == input.TenantGroupId.Value))
            return Result<Tenant>.Validation($"El grupo {input.TenantGroupId} no existe.");

        // Determinar el scheme efectivo
        var scheme = input.SignatureScheme ?? SignatureScheme.WooCommerce;

        // Resolver el secret según las reglas del proyecto
        string secret;
        if (!string.IsNullOrWhiteSpace(input.WebhookSecret))
            secret = input.WebhookSecret!.Trim();
        else if (scheme == SignatureScheme.None)
            secret = string.Empty;
        else
            secret = GenerateWebhookSecret();

        var tenant = new Tenant
        {
            Name            = input.Name.Trim(),
            Slug            = slug,
            WebhookSecret   = secret,
            TargetUrl       = input.TargetUrl.Trim(),
            IsActive        = input.IsActive ?? true,
            SignatureScheme = scheme,
            SignatureHeader = string.IsNullOrWhiteSpace(input.SignatureHeader)
                              ? null
                              : input.SignatureHeader!.Trim(),
            RetryPolicyId  = input.RetryPolicyId,
            TenantGroupId  = input.TenantGroupId,
            CreatedAt      = DateTime.UtcNow
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        _cache.Invalidate(slug);

        _logger.LogInformation("Tenant '{Slug}' creado (esquema {Scheme}).", slug, scheme);

        return Result<Tenant>.Success(tenant);
    }

    /// <summary>
    /// Actualización parcial por slug. El slug es inmutable (no se incluye en el input).
    /// 404 si no existe, 400 si url/FK inválida. Invalida la caché. Devuelve el Tenant actualizado.
    /// </summary>
    public async Task<Result<Tenant>> UpdateAsync(string slug, UpdateTenantInput input)
    {
        var normalized = Tenant.NormalizeSlug(slug);
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Result<Tenant>.NotFound($"Tenant con slug '{slug}' no encontrado.");

        // Validar TargetUrl si se provee
        if (input.TargetUrl is not null)
        {
            if (string.IsNullOrWhiteSpace(input.TargetUrl)
                || !Uri.TryCreate(input.TargetUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Result<Tenant>.Validation("La 'targetUrl' debe ser http:// o https://.");

            tenant.TargetUrl = input.TargetUrl.Trim();
        }

        // Actualizar propiedades opcionales (null = sin cambio)
        if (input.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Result<Tenant>.Validation("El campo 'name' no puede quedar vacío.");
            tenant.Name = input.Name.Trim();
        }

        if (input.SignatureScheme.HasValue)
            tenant.SignatureScheme = input.SignatureScheme.Value;

        if (input.SignatureHeader is not null)
            tenant.SignatureHeader = string.IsNullOrWhiteSpace(input.SignatureHeader)
                ? null
                : input.SignatureHeader.Trim();

        if (input.WebhookSecret is not null)
            tenant.WebhookSecret = input.WebhookSecret.Trim();

        if (input.IsActive.HasValue)
            tenant.IsActive = input.IsActive.Value;

        // FKs: 0 → desasociar; >0 → validar existencia y asignar
        if (input.RetryPolicyId.HasValue)
        {
            if (input.RetryPolicyId.Value == 0)
                tenant.RetryPolicyId = null;
            else if (!await _db.RetryPolicies.AnyAsync(p => p.Id == input.RetryPolicyId.Value))
                return Result<Tenant>.Validation($"La política de reintento {input.RetryPolicyId} no existe.");
            else
                tenant.RetryPolicyId = input.RetryPolicyId.Value;
        }

        if (input.TenantGroupId.HasValue)
        {
            if (input.TenantGroupId.Value == 0)
                tenant.TenantGroupId = null;
            else if (!await _db.TenantGroups.AnyAsync(g => g.Id == input.TenantGroupId.Value))
                return Result<Tenant>.Validation($"El grupo {input.TenantGroupId} no existe.");
            else
                tenant.TenantGroupId = input.TenantGroupId.Value;
        }

        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _cache.Invalidate(normalized);

        _logger.LogInformation("Tenant '{Slug}' actualizado.", normalized);

        return Result<Tenant>.Success(tenant);
    }

    /// <summary>
    /// Actualiza solo la target-url (operación dedicada del endpoint PUT /{slug}/target-url).
    /// 404 si no existe, 400 si url vacía o inválida. Invalida la caché.
    /// Devuelve <see cref="TargetUrlActualizada"/> con el tenant actualizado Y la URL anterior,
    /// para que el endpoint pueda incluir <c>previousUrl</c> sin consultar la base de datos de nuevo.
    /// </summary>
    public async Task<Result<TargetUrlActualizada>> UpdateTargetUrlAsync(string slug, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return Result<TargetUrlActualizada>.Validation("El campo 'targetUrl' es obligatorio.");

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Result<TargetUrlActualizada>.Validation("La URL proporcionada no es válida. Debe ser http:// o https://.");

        var normalized = Tenant.NormalizeSlug(slug);
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Result<TargetUrlActualizada>.NotFound($"Tenant con slug '{slug}' no encontrado.");

        var urlAnterior = tenant.TargetUrl;
        tenant.TargetUrl = targetUrl.Trim();
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _cache.Invalidate(normalized);

        _logger.LogInformation(
            "URL de destino actualizada para tenant '{Slug}': {OldUrl} → {NewUrl}",
            normalized, urlAnterior, tenant.TargetUrl);

        return Result<TargetUrlActualizada>.Success(new TargetUrlActualizada(tenant, urlAnterior));
    }

    /// <summary>
    /// Invierte el estado IsActive de un tenant por su slug.
    /// 404 si no existe. Actualiza UpdatedAt. Invalida la caché. Devuelve el Tenant actualizado.
    /// </summary>
    public async Task<Result<Tenant>> ToggleActiveAsync(string slug)
    {
        var normalized = Tenant.NormalizeSlug(slug);
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Result<Tenant>.NotFound($"Tenant con slug '{slug}' no encontrado.");

        tenant.IsActive = !tenant.IsActive;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _cache.Invalidate(normalized);

        _logger.LogInformation(
            "Tenant '{Slug}' marcado como {Estado}.",
            normalized, tenant.IsActive ? "activo" : "inactivo");

        return Result<Tenant>.Success(tenant);
    }

    /// <summary>
    /// Borra un tenant por su slug (físico, cascadea DeliveryLogs/Attempts por FK+Cascade de EF).
    /// 404 si no existe. Invalida la caché. Devuelve Result (sin valor).
    /// </summary>
    public async Task<Result> DeleteAsync(string slug)
    {
        var normalized = Tenant.NormalizeSlug(slug);
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == normalized);
        if (tenant is null)
            return Result.NotFound($"Tenant con slug '{slug}' no encontrado.");

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync();

        _cache.Invalidate(normalized);

        _logger.LogInformation("Tenant '{Slug}' eliminado.", normalized);

        return Result.Success();
    }

    // ─── Helpers privados ─────────────────────────────────────────────────────

    /// <summary>Genera un WebhookSecret como base64 de 32 bytes aleatorios.</summary>
    private static string GenerateWebhookSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
