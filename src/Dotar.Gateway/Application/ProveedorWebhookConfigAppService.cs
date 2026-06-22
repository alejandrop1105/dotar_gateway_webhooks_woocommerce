using System.Text.Json;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Application;

/// <summary>
/// DTO con las credenciales descifradas del proveedor.
/// Solo se expone en memoria; nunca se serializa ni se devuelve en respuestas HTTP.
/// </summary>
public sealed record CredencialesProveedorDto(
    string AccessToken,
    string SigningSecret,
    string BaseUrl,
    bool IsActive);

/// <summary>
/// DTO extendido que incluye TenantId y credenciales descifradas.
/// Usado por el endpoint de proveedor para resolver tenant + validar firma en una sola consulta.
/// Solo vive en memoria; nunca se serializa.
/// </summary>
public sealed record ProveedorConfigCompletoDto(
    int TenantId,
    string ProveedorNombre,
    string CuentaExternaId,
    string AccessToken,
    string SigningSecret,
    string BaseUrl,
    bool IsActive)
{
    /// <summary>
    /// Redacta credenciales sensibles para que nunca aparezcan en logs estructurados
    /// si este DTO llega accidentalmente a un ToString() (ej. interpolación, Exception.Data).
    /// </summary>
    public override string ToString()
        => $"ProveedorConfigCompletoDto {{ TenantId={TenantId}, ProveedorNombre={ProveedorNombre}, " +
           $"CuentaExternaId={CuentaExternaId}, AccessToken=***, SigningSecret=***, " +
           $"BaseUrl={BaseUrl}, IsActive={IsActive} }}";
}

/// <summary>
/// Estructura JSON interna que se cifra en CredencialesCifradas.
/// Los nombres de propiedad son snake_case para compatibilidad con el JSON
/// que envía el proveedor (ej. "access_token", "signing_secret").
/// </summary>
internal sealed class CredencialesJson
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("signing_secret")]
    public string SigningSecret { get; set; } = string.Empty;
}

/// <summary>
/// Servicio de aplicación para la configuración de proveedores de webhooks (Scoped).
/// Cifra las credenciales con IDataProtector antes de persistir.
/// Los DTOs de respuesta nunca exponen credenciales en claro.
/// </summary>
public sealed class ProveedorWebhookConfigAppService
{
    private const string DataProtectorPurpose = "ProveedorWebhookConfig.Credenciales.v1";

    private readonly GatewayDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<ProveedorWebhookConfigAppService> _logger;

    public ProveedorWebhookConfigAppService(
        GatewayDbContext db,
        IDataProtectionProvider dataProtection,
        ILogger<ProveedorWebhookConfigAppService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(DataProtectorPurpose);
        _logger = logger;
    }

    /// <summary>
    /// Crea o actualiza la configuración de un proveedor para un tenant.
    /// Cifra el JSON de credenciales antes de persistir.
    /// El upsert es idempotente por (TenantId, ProveedorNombre).
    /// </summary>
    public async Task<Result> UpsertAsync(
        int tenantId,
        string proveedorNombre,
        string cuentaExternaId,
        string credencialesJson,
        string baseUrl,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(proveedorNombre))
            return Result.Validation("El campo 'proveedorNombre' es obligatorio.");

        if (string.IsNullOrWhiteSpace(cuentaExternaId))
            return Result.Validation("El campo 'cuentaExternaId' es obligatorio.");

        if (string.IsNullOrWhiteSpace(credencialesJson))
            return Result.Validation("El campo 'credencialesJson' es obligatorio.");

        // Cifrar credenciales
        string ciphertext;
        try
        {
            ciphertext = _protector.Protect(credencialesJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cifrar credenciales del proveedor '{Proveedor}'.", proveedorNombre);
            return Result.Failure(ResultError.Validation, "No se pudieron cifrar las credenciales.");
        }

        // Upsert por (TenantId, ProveedorNombre) — un proveedor por tenant en v1
        var config = await _db.ProveedoresWebhookConfig
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProveedorNombre == proveedorNombre);

        var ahora = DateTime.UtcNow;

        if (config is null)
        {
            config = new ProveedorWebhookConfig
            {
                TenantId = tenantId,
                ProveedorNombre = proveedorNombre,
                CuentaExternaId = cuentaExternaId,
                CredencialesCifradas = ciphertext,
                BaseUrl = baseUrl,
                IsActive = isActive,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            _db.ProveedoresWebhookConfig.Add(config);
        }
        else
        {
            config.CuentaExternaId = cuentaExternaId;
            config.CredencialesCifradas = ciphertext;
            config.BaseUrl = baseUrl;
            config.IsActive = isActive;
            config.UpdatedAt = ahora;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Config del proveedor '{Proveedor}' para tenant {TenantId} actualizada.",
            proveedorNombre, tenantId);

        return Result.Success();
    }

    /// <summary>
    /// Busca la config por (ProveedorNombre, CuentaExternaId) — lookup inverso desde webhook entrante.
    /// Descifra las credenciales en memoria. Retorna null si no existe.
    /// </summary>
    public async Task<CredencialesProveedorDto?> GetByProveedorYCuentaAsync(
        string proveedorNombre,
        string cuentaExternaId)
    {
        var config = await _db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.ProveedorNombre == proveedorNombre &&
                p.CuentaExternaId == cuentaExternaId);

        return config is null ? null : Descifrar(config);
    }

    /// <summary>
    /// Busca la config por (TenantId, ProveedorNombre).
    /// Descifra las credenciales en memoria. Retorna null si no existe.
    /// </summary>
    public async Task<CredencialesProveedorDto?> GetByTenantYProveedorAsync(
        int tenantId,
        string proveedorNombre)
    {
        var config = await _db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.TenantId == tenantId &&
                p.ProveedorNombre == proveedorNombre);

        return config is null ? null : Descifrar(config);
    }

    /// <summary>
    /// Busca la config por (ProveedorNombre, CuentaExternaId) y devuelve TenantId + credenciales descifradas.
    /// Lookup inverso usado por el endpoint de proveedor en una sola consulta DB.
    /// </summary>
    public async Task<ProveedorConfigCompletoDto?> GetCompletoByProveedorYCuentaAsync(
        string proveedorNombre,
        string cuentaExternaId)
    {
        var config = await _db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.ProveedorNombre == proveedorNombre &&
                p.CuentaExternaId == cuentaExternaId &&
                p.IsActive);

        if (config is null) return null;

        var creds = Descifrar(config);
        return new ProveedorConfigCompletoDto(
            config.TenantId,
            config.ProveedorNombre,
            config.CuentaExternaId,
            creds.AccessToken,
            creds.SigningSecret,
            config.BaseUrl,
            config.IsActive);
    }

    // ─── Helpers privados ─────────────────────────────────────────────────────

    private CredencialesProveedorDto Descifrar(ProveedorWebhookConfig config)
    {
        string accessToken = string.Empty;
        string signingSecret = string.Empty;

        try
        {
            var plaintext = _protector.Unprotect(config.CredencialesCifradas);
            var creds = JsonSerializer.Deserialize<CredencialesJson>(plaintext,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            accessToken = creds?.AccessToken ?? string.Empty;
            signingSecret = creds?.SigningSecret ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error al descifrar credenciales del proveedor '{Proveedor}' (tenant {TenantId}).",
                config.ProveedorNombre, config.TenantId);
        }

        return new CredencialesProveedorDto(accessToken, signingSecret, config.BaseUrl, config.IsActive);
    }
}
