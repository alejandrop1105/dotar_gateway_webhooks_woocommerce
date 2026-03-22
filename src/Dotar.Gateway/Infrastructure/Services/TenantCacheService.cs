using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Caché de configuración de tenants en memoria para evitar hits a SQLite
/// en cada webhook entrante. Soporta invalidación manual desde el Dashboard.
/// </summary>
public class TenantCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenantCacheService> _logger;
    private readonly TimeSpan _expiration;

    // SemaphoreSlim por slug para evitar cache stampede
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public TenantCacheService(
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TenantCacheService> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int>("Gateway:TenantCacheMinutes");
        _expiration = TimeSpan.FromMinutes(minutes > 0 ? minutes : 5);
    }

    /// <summary>
    /// Obtiene un tenant por slug, usando caché con patrón cache-aside.
    /// </summary>
    public async Task<Tenant?> GetBySlugAsync(string slug)
    {
        var cacheKey = $"tenant:{slug}";

        if (_cache.TryGetValue(cacheKey, out Tenant? cached))
            return cached;

        await _semaphore.WaitAsync();
        try
        {
            // Double-check después de adquirir el lock
            if (_cache.TryGetValue(cacheKey, out cached))
                return cached;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var tenant = await db.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug);

            if (tenant is not null)
            {
                _cache.Set(cacheKey, tenant, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = _expiration
                });
            }

            return tenant;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Invalida la caché de un tenant específico (llamado desde el Dashboard al editar).
    /// </summary>
    public void Invalidate(string slug)
    {
        _cache.Remove($"tenant:{slug}");
        _logger.LogInformation("Caché invalidada para tenant {Slug}", slug);
    }
}
