using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Caché de cajas registradas en memoria para evitar hits a SQLite en el hot path del worker.
/// Singleton — abre scope por operación para resolver GatewayDbContext (Scoped).
/// Excluye cajas cuya UltimaVez sea más vieja que Seguridad:CajaTtlMinutos.
/// </summary>
public class CajaRegistradaCacheService : ICajaRegistradaCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CajaRegistradaCacheService> _logger;
    private readonly TimeSpan _ttl;

    // SemaphoreSlim estático para evitar cache stampede en la misma key.
    // En producción con N instancias por proceso, uno por par (tenantId,identificador)
    // sería ideal, pero aquí usamos uno global por simplicidad (igual que TenantCacheService).
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public CajaRegistradaCacheService(
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CajaRegistradaCacheService> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int>("Seguridad:CajaTtlMinutos");
        _ttl = TimeSpan.FromMinutes(minutes > 0 ? minutes : 30);
    }

    /// <summary>
    /// Obtiene una caja por (TenantId, Identificador) usando cache-aside.
    /// En caso de miss, consulta la DB abriendo un scope temporal.
    /// No retorna cajas cuya UltimaVez sea nula o supere el TTL configurado.
    /// </summary>
    public async Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador)
    {
        var cacheKey = BuildKey(tenantId, identificador);

        if (_cache.TryGetValue(cacheKey, out CajaRegistrada? cached))
            return cached;

        await _semaphore.WaitAsync();
        try
        {
            // Double-check después de adquirir el lock
            if (_cache.TryGetValue(cacheKey, out cached))
                return cached;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var caja = await db.CajasRegistradas.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Identificador == identificador);

            // Excluir cajas "inactivas" (UltimaVez muy antigua — heartbeat vencido)
            if (caja is not null && !EsVigente(caja))
            {
                _logger.LogDebug(
                    "Caja '{Identificador}' del tenant {TenantId} excluida: UltimaVez vencida.",
                    identificador, tenantId);
                return null;
            }

            if (caja is not null)
            {
                _cache.Set(cacheKey, caja, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = _ttl
                });
            }

            return caja;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Invalida la entrada de caché para la caja indicada.
    /// Llamado por CajaRegistradaAppService tras cada upsert.
    /// </summary>
    public void Invalidate(int tenantId, string identificador)
    {
        var key = BuildKey(tenantId, identificador);
        _cache.Remove(key);
        _logger.LogDebug("Caché de caja invalidada: tenantId={TenantId}, identificador={Id}",
            tenantId, identificador);
    }

    // ─── Helpers privados ─────────────────────────────────────────────────────

    private static string BuildKey(int tenantId, string identificador)
        => $"caja:{tenantId}:{identificador}";

    /// <summary>
    /// Una caja es vigente si su UltimaVez no es nula y no supera el TTL.
    /// El gateway considera "vencida" una caja que no hizo heartbeat en mucho tiempo.
    /// </summary>
    private bool EsVigente(CajaRegistrada caja)
    {
        if (caja.UltimaVez is null)
            return false;

        return DateTime.UtcNow - caja.UltimaVez.Value <= _ttl;
    }
}
