using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de TenantAppService.DeleteAsync.
/// GatewayDbContext usa SQLite en memoria (igual que los otros tests del AppService).
/// </summary>
public class TenantAppServiceDeleteTests : IDisposable
{
    private sealed class FakeCacheService : ITenantCacheService
    {
        public int InvalidateCount { get; private set; }
        public string? LastInvalidatedSlug { get; private set; }

        public void Invalidate(string slug)
        {
            InvalidateCount++;
            LastInvalidatedSlug = slug;
        }

        public Task<Tenant?> GetBySlugAsync(string slug) => Task.FromResult<Tenant?>(null);
    }

    private readonly GatewayDbContext _db;
    private readonly FakeCacheService _cache;
    private readonly TenantAppService _svc;

    private readonly string _dbPath;

    public TenantAppServiceDeleteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-delete-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new GatewayDbContext(opts);
        _db.Database.EnsureCreated();

        _cache = new FakeCacheService();
        _svc = new TenantAppService(_db, _cache, NullLogger<TenantAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private async Task<Tenant> SeedTenant(string slug = "test-tenant", bool isActive = true)
    {
        var tenant = new Tenant
        {
            Name            = "Test Tenant",
            Slug            = slug,
            TargetUrl       = "https://example.com/hooks",
            WebhookSecret   = "secret",
            IsActive        = isActive,
            SignatureScheme = SignatureScheme.WooCommerce,
            CreatedAt       = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    // ─── Not Found ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_TenantNoExiste_DevuelveNotFound()
    {
        var result = await _svc.DeleteAsync("no-existe");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task DeleteAsync_TenantNoExiste_NoCacheInvalidada()
    {
        await _svc.DeleteAsync("no-existe");

        Assert.Equal(0, _cache.InvalidateCount);
    }

    // ─── Éxito ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_TenantExistente_DevuelveSuccess()
    {
        await SeedTenant("borrar-ok");

        var result = await _svc.DeleteAsync("borrar-ok");

        Assert.True(result.IsSuccess);
        Assert.Equal(ResultError.None, result.Error);
    }

    [Fact]
    public async Task DeleteAsync_TenantExistente_EliminaDeDb()
    {
        await SeedTenant("borrar-db");

        await _svc.DeleteAsync("borrar-db");

        var enDb = await _db.Tenants.AnyAsync(t => t.Slug == "borrar-db");
        Assert.False(enDb);
    }

    [Fact]
    public async Task DeleteAsync_TenantExistente_InvalidaCache()
    {
        await SeedTenant("borrar-cache");

        await _svc.DeleteAsync("borrar-cache");

        Assert.Equal(1, _cache.InvalidateCount);
        Assert.Equal("borrar-cache", _cache.LastInvalidatedSlug);
    }

    [Fact]
    public async Task DeleteAsync_SlugConMayusculas_NormalizaYBorra()
    {
        await SeedTenant("borrar-upper");

        // Pasar el slug con mayúsculas; debe normalizar y encontrarlo
        var result = await _svc.DeleteAsync("BORRAR-UPPER");

        Assert.True(result.IsSuccess);
        var enDb = await _db.Tenants.AnyAsync(t => t.Slug == "borrar-upper");
        Assert.False(enDb);
    }

    [Fact]
    public async Task DeleteAsync_TenantInactivo_SeBorraTambien()
    {
        await SeedTenant("borrar-inactivo", isActive: false);

        var result = await _svc.DeleteAsync("borrar-inactivo");

        Assert.True(result.IsSuccess);
        var enDb = await _db.Tenants.AnyAsync(t => t.Slug == "borrar-inactivo");
        Assert.False(enDb);
    }
}
