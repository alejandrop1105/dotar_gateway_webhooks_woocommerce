using System.Security.Cryptography;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Application;

/// <summary>
/// Datos para crear un tenant.
/// Equivale a CreateTenantRequest pero sin acoplar a la capa HTTP.
/// Los 4 campos de ruteo son opcionales al final del record para mantener compat con el ERP.
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
    int? TenantGroupId = null,
    bool? RuteoProveedorActivo = null,
    string? ProveedorRuteoNombre = null,
    string? SucursalMetaKey = null,
    string? SucursalMetaSeparador = null);

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
/// Los 4 campos de ruteo son opcionales; null significa "no tocar".
/// RuteoProveedorActivo=false (explícito) fuerza la limpieza de los 3 campos dependientes.
/// </summary>
public sealed record UpdateTenantInput(
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

/// <summary>
/// Servicio de aplicación para operaciones de Tenant (Scoped).
/// Centraliza toda la lógica de negocio de tenants: validación de slug, normalización,
/// unicidad, validación de FKs, generación de secret, invalidación de caché,
/// y validación de ruteo multi-sucursal.
/// Los consumidores (endpoints Minimal API y componentes Blazor) delegan en este servicio.
/// </summary>
public sealed class TenantAppService
{
    private readonly GatewayDbContext _db;
    private readonly ITenantCacheService _cache;
    private readonly ILogger<TenantAppService> _logger;
    private readonly IProveedorRuteoCatalog? _catalog;

    /// <param name="db">Contexto de EF Core.</param>
    /// <param name="cache">Servicio de caché de tenants.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="catalog">
    /// Catálogo de proveedores de ruteo. Parámetro opcional para no romper
    /// los tests existentes que construyen el servicio con 3 argumentos.
    /// Si es null, el catálogo se considera vacío (ningún proveedor es válido).
    /// </param>
    public TenantAppService(
        GatewayDbContext db,
        ITenantCacheService cache,
        ILogger<TenantAppService> logger,
        IProveedorRuteoCatalog? catalog = null)
    {
        _db      = db;
        _cache   = cache;
        _logger  = logger;
        _catalog = catalog;
    }

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

        // Calcular estado efectivo de ruteo para la creación.
        // Si el ruteo está inactivo, los 3 dependientes se limpian (misma semántica que UpdateAsync).
        var ruteoActivo = input.RuteoProveedorActivo ?? false;
        var proveedorNombre       = ruteoActivo ? (input.ProveedorRuteoNombre  ?? string.Empty) : string.Empty;
        var sucursalMetaKey       = ruteoActivo ? (input.SucursalMetaKey       ?? string.Empty) : string.Empty;
        var sucursalMetaSeparador = ruteoActivo ? (input.SucursalMetaSeparador ?? string.Empty) : string.Empty;

        // Validar ruteo sobre el estado efectivo final (solo si activo).
        var validacionRuteo = ValidarRuteo(ruteoActivo, proveedorNombre, sucursalMetaKey);
        if (validacionRuteo is not null)
            return Result<Tenant>.Validation(validacionRuteo);

        var tenant = new Tenant
        {
            Name                  = input.Name.Trim(),
            Slug                  = slug,
            WebhookSecret         = secret,
            TargetUrl             = input.TargetUrl.Trim(),
            IsActive              = input.IsActive ?? true,
            SignatureScheme       = scheme,
            SignatureHeader       = string.IsNullOrWhiteSpace(input.SignatureHeader)
                                    ? null
                                    : input.SignatureHeader!.Trim(),
            RetryPolicyId         = input.RetryPolicyId,
            TenantGroupId         = input.TenantGroupId,
            CreatedAt             = DateTime.UtcNow,
            RuteoProveedorActivo  = ruteoActivo,
            ProveedorRuteoNombre  = string.IsNullOrEmpty(proveedorNombre) ? null : proveedorNombre,
            SucursalMetaKey       = string.IsNullOrEmpty(sucursalMetaKey) ? null : sucursalMetaKey,
            SucursalMetaSeparador = string.IsNullOrEmpty(sucursalMetaSeparador) ? null : sucursalMetaSeparador
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

        // ─── Ruteo: semántica update-parcial ─────────────────────────────────
        // Si RuteoProveedorActivo es false explícito → limpiar los 3 dependientes.
        // Si RuteoProveedorActivo es true → aplicar cambios de dependientes si se proveen.
        // Si RuteoProveedorActivo es null → no tocar el flag ni los dependientes
        //   (a menos que se provean individualmente).

        if (input.RuteoProveedorActivo.HasValue)
        {
            if (!input.RuteoProveedorActivo.Value)
            {
                // Apagado explícito: limpiar el flag y los 3 dependientes.
                tenant.RuteoProveedorActivo  = false;
                tenant.ProveedorRuteoNombre  = null;
                tenant.SucursalMetaKey       = null;
                tenant.SucursalMetaSeparador = null;
            }
            else
            {
                // Activar: aplicar dependientes si se proveen (null = sin cambio).
                tenant.RuteoProveedorActivo = true;
                if (input.ProveedorRuteoNombre is not null)
                    tenant.ProveedorRuteoNombre = string.IsNullOrEmpty(input.ProveedorRuteoNombre)
                        ? null : input.ProveedorRuteoNombre;
                if (input.SucursalMetaKey is not null)
                    tenant.SucursalMetaKey = string.IsNullOrEmpty(input.SucursalMetaKey)
                        ? null : input.SucursalMetaKey;
                if (input.SucursalMetaSeparador is not null)
                    tenant.SucursalMetaSeparador = string.IsNullOrEmpty(input.SucursalMetaSeparador)
                        ? null : input.SucursalMetaSeparador;
            }
        }
        else
        {
            // RuteoProveedorActivo null: actualizar dependientes individualmente si se proveen.
            if (input.ProveedorRuteoNombre is not null)
                tenant.ProveedorRuteoNombre = string.IsNullOrEmpty(input.ProveedorRuteoNombre)
                    ? null : input.ProveedorRuteoNombre;
            if (input.SucursalMetaKey is not null)
                tenant.SucursalMetaKey = string.IsNullOrEmpty(input.SucursalMetaKey)
                    ? null : input.SucursalMetaKey;
            if (input.SucursalMetaSeparador is not null)
                tenant.SucursalMetaSeparador = string.IsNullOrEmpty(input.SucursalMetaSeparador)
                    ? null : input.SucursalMetaSeparador;
        }

        // Validar ruteo sobre el ESTADO EFECTIVO final del tenant (post-patch)
        var validacionRuteoUpdate = ValidarRuteo(
            tenant.RuteoProveedorActivo,
            tenant.ProveedorRuteoNombre ?? string.Empty,
            tenant.SucursalMetaKey ?? string.Empty);
        if (validacionRuteoUpdate is not null)
            return Result<Tenant>.Validation(validacionRuteoUpdate);

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

    /// <summary>
    /// Valida el estado efectivo de los campos de ruteo.
    /// Se llama DESPUÉS de aplicar el input al estado del tenant (estado final).
    /// Devuelve el mensaje de error si hay fallo, null si es válido.
    /// </summary>
    private string? ValidarRuteo(bool ruteoActivo, string proveedorNombre, string sucursalMetaKey)
    {
        if (!ruteoActivo)
            return null; // Sin ruteo activo no hay nada que validar.

        if (string.IsNullOrWhiteSpace(proveedorNombre))
            return "El campo 'ProveedorRuteoNombre' es obligatorio cuando RuteoProveedorActivo está activo.";

        if (string.IsNullOrWhiteSpace(sucursalMetaKey))
            return "El campo 'SucursalMetaKey' es obligatorio cuando RuteoProveedorActivo está activo.";

        // Validar que el proveedor esté en el catálogo (si el catálogo está inyectado).
        var keysValidas = _catalog?.KeysValidas ?? [];
        if (!keysValidas.Contains(proveedorNombre))
            return $"El proveedor '{proveedorNombre}' no está registrado. Proveedores válidos: {string.Join(", ", keysValidas)}.";

        return null;
    }

    /// <summary>Genera un WebhookSecret como base64 de 32 bytes aleatorios.</summary>
    private static string GenerateWebhookSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
