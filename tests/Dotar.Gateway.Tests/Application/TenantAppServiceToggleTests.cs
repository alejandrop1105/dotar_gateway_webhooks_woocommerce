using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de TenantAppService.ToggleActiveAsync.
/// </summary>
public class TenantAppServiceToggleTests : IDisposable
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

    public TenantAppServiceToggleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-toggle-{Guid.NewGuid():N}.db");
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

    private async Task<Tenant> SeedTenant(string slug, bool isActive)
    {
        var tenant = new Tenant
        {
            Name            = "Toggle Test",
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

    // ─── Not Found ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_TenantNoExiste_DevuelveNotFound()
    {
        var result = await _svc.ToggleActiveAsync("no-existe-toggle");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task ToggleActiveAsync_TenantNoExiste_NoCacheInvalidada()
    {
        await _svc.ToggleActiveAsync("no-existe-toggle-cache");

        Assert.Equal(0, _cache.InvalidateCount);
    }

    // ─── Inversión de estado ─────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_TenantActivo_QueaInactivo()
    {
        await SeedTenant("toggle-activo", isActive: true);

        var result = await _svc.ToggleActiveAsync("toggle-activo");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsActive);
    }

    [Fact]
    public async Task ToggleActiveAsync_TenantInactivo_QueaActivo()
    {
        await SeedTenant("toggle-inactivo", isActive: false);

        var result = await _svc.ToggleActiveAsync("toggle-inactivo");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsActive);
    }

    // ─── UpdatedAt ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_Exitoso_EstableceUpdatedAtUtc()
    {
        await SeedTenant("toggle-updatedat", isActive: true);
        var antes = DateTime.UtcNow.AddSeconds(-1);

        var result = await _svc.ToggleActiveAsync("toggle-updatedat");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.UpdatedAt);
        Assert.True(result.Value!.UpdatedAt >= antes);
    }

    // ─── Caché ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_Exitoso_InvalidaCache()
    {
        await SeedTenant("toggle-cache", isActive: true);

        await _svc.ToggleActiveAsync("toggle-cache");

        Assert.Equal(1, _cache.InvalidateCount);
        Assert.Equal("toggle-cache", _cache.LastInvalidatedSlug);
    }

    [Fact]
    public async Task ToggleActiveAsync_Exitoso_InvalidaCacheExactamenteUnaVez()
    {
        await SeedTenant("toggle-cache-once", isActive: false);

        await _svc.ToggleActiveAsync("toggle-cache-once");

        Assert.Equal(1, _cache.InvalidateCount);
    }

    // ─── Persistencia ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_Exitoso_PersisteCambioEnDb()
    {
        await SeedTenant("toggle-persist", isActive: true);

        await _svc.ToggleActiveAsync("toggle-persist");

        var enDb = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Slug == "toggle-persist");
        Assert.False(enDb.IsActive);
    }

    // ─── Normalización de slug ───────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_SlugConMayusculas_NormalizaYToggle()
    {
        await SeedTenant("toggle-upper", isActive: true);

        var result = await _svc.ToggleActiveAsync("TOGGLE-UPPER");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsActive);
    }

    // ─── Valor de retorno ────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleActiveAsync_Exitoso_DevuelveTenantActualizado()
    {
        await SeedTenant("toggle-return", isActive: false);

        var result = await _svc.ToggleActiveAsync("toggle-return");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("toggle-return", result.Value!.Slug);
        Assert.True(result.Value!.IsActive);
    }
}
