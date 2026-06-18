using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de TenantAppService.UpdateTargetUrlAsync.
/// Usa GatewayDbContext con SQLite en memoria y un fake de ITenantCacheService.
/// </summary>
public class TenantAppServiceUpdateTargetUrlTests : IDisposable
{
    // ─── Infraestructura de test ──────────────────────────────────────────────

    private sealed class FakeCacheService : ITenantCacheService
    {
        public List<string> InvalidatedSlugs { get; } = [];

        public void Invalidate(string slug) => InvalidatedSlugs.Add(slug);

        public Task<Tenant?> GetBySlugAsync(string slug) => Task.FromResult<Tenant?>(null);
    }

    private readonly GatewayDbContext _db;
    private readonly FakeCacheService _cache;
    private readonly TenantAppService _service;

    public TenantAppServiceUpdateTargetUrlTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-target-url-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _cache = new FakeCacheService();
        _service = new TenantAppService(_db, _cache, NullLogger<TenantAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task<Tenant> CrearTenantBase(string slug = "mi-tenant")
    {
        var tenant = new Tenant
        {
            Name            = "Mi Tenant",
            Slug            = slug,
            TargetUrl       = "https://original.com/api/webhooks",
            WebhookSecret   = "secret",
            IsActive        = true,
            SignatureScheme = SignatureScheme.WooCommerce,
            CreatedAt       = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        _cache.InvalidatedSlugs.Clear();
        return tenant;
    }

    // ─── Not Found ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTargetUrlAsync_SlugNoExiste_DevuelveNotFound()
    {
        var result = await _service.UpdateTargetUrlAsync("no-existe", "https://nueva.com/h");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
    }

    // ─── Validación de URL ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlVacia_DevuelveValidation()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlSoloWhitespace_DevuelveValidation()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "   ");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlInvalida_DevuelveValidation()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "no-es-una-url");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlFtp_DevuelveValidation()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "ftp://no-permitido.com/x");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Actualización exitosa ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlValida_DevuelveIsSuccessYActualiza()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "https://nueva.com/webhooks");

        Assert.True(result.IsSuccess);
        Assert.Equal(ResultError.None, result.Error);
        Assert.Equal("https://nueva.com/webhooks", result.Value!.Tenant.TargetUrl);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_Exitoso_DevuelvePreviousUrlCorrecta()
    {
        await CrearTenantBase(); // TargetUrl original = "https://original.com/api/webhooks"

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "https://nueva.com/webhooks");

        Assert.True(result.IsSuccess);
        Assert.Equal("https://original.com/api/webhooks", result.Value!.PreviousUrl);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_Exitoso_UpdatedAtEsUtcPosteriorAlInicio()
    {
        await CrearTenantBase();
        var antes = DateTime.UtcNow.AddMilliseconds(-50);

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "https://nueva.com/h");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.Tenant.UpdatedAt);
        Assert.True(result.Value!.Tenant.UpdatedAt!.Value >= antes,
            $"UpdatedAt ({result.Value!.Tenant.UpdatedAt}) debería ser >= {antes}");
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_Exitoso_PersisteCambioEnDb()
    {
        await CrearTenantBase(slug: "persist-test");

        await _service.UpdateTargetUrlAsync("persist-test", "https://nueva.com/h");

        var enDb = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == "persist-test");
        Assert.NotNull(enDb);
        Assert.Equal("https://nueva.com/h", enDb!.TargetUrl);
    }

    // ─── Invalidación de caché ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTargetUrlAsync_Exitoso_InvalidaCacheExactamenteUnaVez()
    {
        await CrearTenantBase(slug: "cache-target");

        var result = await _service.UpdateTargetUrlAsync("cache-target", "https://nueva.com/h");

        Assert.True(result.IsSuccess);
        Assert.Single(_cache.InvalidatedSlugs);
        Assert.Equal("cache-target", _cache.InvalidatedSlugs[0]);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_NotFound_NoCacheInvalidada()
    {
        var result = await _service.UpdateTargetUrlAsync("no-existe", "https://nueva.com/h");

        Assert.False(result.IsSuccess);
        Assert.Empty(_cache.InvalidatedSlugs);
    }

    [Fact]
    public async Task UpdateTargetUrlAsync_UrlInvalida_NoCacheInvalidada()
    {
        await CrearTenantBase();

        var result = await _service.UpdateTargetUrlAsync("mi-tenant", "url-mala");

        Assert.False(result.IsSuccess);
        Assert.Empty(_cache.InvalidatedSlugs);
    }
}
