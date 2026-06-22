using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Application;

/// <summary>
/// DTO de retorno de una caja registrada.
/// No incluye campos de infraestructura interna.
/// </summary>
public sealed record CajaDto(
    long Id,
    int TenantId,
    string Identificador,
    string CallbackUrl,
    DateTime? UltimaVez,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Servicio de aplicación para el padrón de cajas registradas (Scoped).
/// Centraliza la lógica de auto-registro: validación anti-SSRF, allowlist de dominios,
/// upsert idempotente e invalidación de caché.
/// </summary>
public sealed class CajaRegistradaAppService
{
    private readonly GatewayDbContext _db;
    private readonly ICajaRegistradaCacheService _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<CajaRegistradaAppService> _logger;

    public CajaRegistradaAppService(
        GatewayDbContext db,
        ICajaRegistradaCacheService cache,
        IConfiguration config,
        ILogger<CajaRegistradaAppService> logger)
        => (_db, _cache, _config, _logger) = (db, cache, config, logger);

    /// <summary>
    /// Registra o actualiza una caja del tenant en el padrón.
    /// Valida: callbackUrl debe ser https:// y su host debe coincidir con la allowlist.
    /// El upsert es idempotente por (TenantId, Identificador).
    /// </summary>
    public async Task<Result<CajaDto>> RegistrarAsync(
        int tenantId,
        string identificador,
        string callbackUrl)
    {
        // Validaciones básicas
        if (string.IsNullOrWhiteSpace(identificador))
            return Result<CajaDto>.Validation("El campo 'identificador' es obligatorio.");

        if (identificador.Contains("::"))
            return Result<CajaDto>.Validation("El 'identificador' no puede contener '::'.");

        if (identificador.Length > 100)
            return Result<CajaDto>.Validation(
                "El 'identificador' no puede superar los 100 caracteres.");

        if (string.IsNullOrWhiteSpace(callbackUrl))
            return Result<CajaDto>.Validation("El campo 'callbackUrl' es obligatorio.");

        if (callbackUrl.Length > 2000)
            return Result<CajaDto>.Validation(
                "La 'callbackUrl' no puede superar los 2000 caracteres.");

        // Validación anti-SSRF: debe ser https://
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return Result<CajaDto>.Validation(
                "La 'callbackUrl' debe ser https://.");

        // Validación anti-SSRF: rechazar puerto no estándar (solo 443 permitido para https)
        if (uri.Port != 443)
            return Result<CajaDto>.Validation(
                "La 'callbackUrl' no puede especificar un puerto no estándar.");

        // Validar contra allowlist de dominios
        var allowList = _config.GetSection("Seguridad:CallbackDominiosPermitidos")
            .GetChildren()
            .Select(c => c.Value ?? string.Empty)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToArray();

        if (!EsHostPermitido(uri.Host, allowList))
            return Result<CajaDto>.Validation(
                $"La 'callbackUrl' tiene un dominio no permitido: '{uri.Host}'.");

        // Upsert idempotente por (TenantId, Identificador)
        var caja = await _db.CajasRegistradas
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Identificador == identificador);

        var ahora = DateTime.UtcNow;

        if (caja is null)
        {
            caja = new CajaRegistrada
            {
                TenantId = tenantId,
                Identificador = identificador,
                CallbackUrl = callbackUrl,
                UltimaVez = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            _db.CajasRegistradas.Add(caja);
        }
        else
        {
            caja.CallbackUrl = callbackUrl;
            caja.UltimaVez = ahora;
            caja.UpdatedAt = ahora;
        }

        await _db.SaveChangesAsync();

        _cache.Invalidate(tenantId, identificador);

        _logger.LogInformation(
            "Caja '{Identificador}' del tenant {TenantId} registrada/actualizada en el padrón.",
            identificador, tenantId);

        return Result<CajaDto>.Success(ToDto(caja));
    }

    // ─── Helpers privados ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifica que el host esté en la allowlist.
    /// Soporta comodín de subdominio: *.dominio.com coincide con cualquier.dominio.com.
    /// </summary>
    private static bool EsHostPermitido(string host, string[] allowList)
    {
        foreach (var entry in allowList)
        {
            if (entry.StartsWith("*.", StringComparison.OrdinalIgnoreCase))
            {
                // Comodín: el host debe terminar en ".sufijo" o ser exactamente "sufijo"
                var sufijo = entry[1..]; // ".dominio.com"
                if (host.EndsWith(sufijo, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                if (host.Equals(entry, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static CajaDto ToDto(CajaRegistrada c) =>
        new(c.Id, c.TenantId, c.Identificador, c.CallbackUrl,
            c.UltimaVez, c.CreatedAt, c.UpdatedAt);
}
