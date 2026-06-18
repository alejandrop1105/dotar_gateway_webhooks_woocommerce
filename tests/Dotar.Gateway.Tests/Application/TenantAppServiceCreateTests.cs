using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de TenantAppService.CreateAsync.
/// Usa GatewayDbContext con SQLite en memoria y un fake de ITenantCacheService.
/// </summary>
public class TenantAppServiceCreateTests : IDisposable
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

    public TenantAppServiceCreateTests()
    {
        // Cada test class usa un archivo de DB temporal único para aislamiento.
        // SQLite con archivo temporal es más confiable que :memory: con EF Core.
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-create-{Guid.NewGuid():N}.db");
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

    private static CreateTenantInput ValidInput(
        string name = "Mi Tenant",
        string slug = "mi-tenant",
        string targetUrl = "https://destino.com/api/webhooks",
        SignatureScheme? scheme = null) =>
        new(Name: name, Slug: slug, TargetUrl: targetUrl,
            SignatureScheme: scheme ?? SignatureScheme.WooCommerce);

    // ─── Validación de campos requeridos ─────────────────────────────────────

    [Fact]
    public async Task CreateAsync_NameVacio_DevuelveValidation()
    {
        var input = ValidInput(name: "");
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task CreateAsync_SlugVacio_DevuelveValidation()
    {
        var input = ValidInput(slug: "");
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task CreateAsync_TargetUrlVacia_DevuelveValidation()
    {
        var input = ValidInput(targetUrl: "");
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Validación de slug ───────────────────────────────────────────────────

    [Theory]
    [InlineData("with space")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("with/slash")]
    public async Task CreateAsync_SlugInvalidoTrasNormalizacion_DevuelveValidation(string slug)
    {
        var input = ValidInput(slug: slug);
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Normalización de slug ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_SlugConEspaciosYMayusculas_AlmacenaNormalizado()
    {
        var input = ValidInput(slug: " Mi-Tenant ");
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("mi-tenant", result.Value!.Slug);

        var dbTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == "mi-tenant");
        Assert.NotNull(dbTenant);
    }

    // ─── Unicidad de slug ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_SlugDuplicado_DevuelveConflict()
    {
        // Crear el primero
        await _service.CreateAsync(ValidInput(slug: "duplicado"));

        // Intentar crear con el mismo slug
        var result = await _service.CreateAsync(ValidInput(slug: "duplicado"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Conflict, result.Error);
    }

    // ─── Validación de FKs ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_RetryPolicyIdInexistente_DevuelveValidation()
    {
        var input = new CreateTenantInput(
            Name: "Test", Slug: "fk-retry", TargetUrl: "https://x.com/h",
            RetryPolicyId: 9999);
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task CreateAsync_TenantGroupIdInexistente_DevuelveValidation()
    {
        var input = new CreateTenantInput(
            Name: "Test", Slug: "fk-group", TargetUrl: "https://x.com/h",
            TenantGroupId: 9999);
        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Generación de secret ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_EsquemaNone_WebhookSecretVacio()
    {
        var input = ValidInput(scheme: SignatureScheme.None);
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Value!.WebhookSecret);
    }

    [Fact]
    public async Task CreateAsync_EsquemaHmac_WebhookSecretBase64Autogenerado()
    {
        var input = ValidInput(scheme: SignatureScheme.WooCommerce);
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        var secret = result.Value!.WebhookSecret;
        Assert.False(string.IsNullOrWhiteSpace(secret));

        // Base64 de 32 bytes = 44 chars
        var bytes = Convert.FromBase64String(secret);
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public async Task CreateAsync_SecretExplicito_UsaElProvisto()
    {
        var input = new CreateTenantInput(
            Name: "Test", Slug: "secret-explicito", TargetUrl: "https://x.com/h",
            WebhookSecret: "mi-secret-personalizado",
            SignatureScheme: SignatureScheme.WooCommerce);
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("mi-secret-personalizado", result.Value!.WebhookSecret);
    }

    // ─── Invalidación de caché ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Exitoso_InvalidaCacheExactamenteUnaVez()
    {
        var input = ValidInput(slug: "cache-test");
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Single(_cache.InvalidatedSlugs);
        Assert.Equal("cache-test", _cache.InvalidatedSlugs[0]);
    }

    [Fact]
    public async Task CreateAsync_FalloValidacion_NoCacheInvalidada()
    {
        var input = ValidInput(name: "");  // nombre vacío → validation
        await _service.CreateAsync(input);

        Assert.Empty(_cache.InvalidatedSlugs);
    }

    // ─── Happy path completo ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DatosValidos_DevuelveIsSuccessYPersiste()
    {
        var input = ValidInput();
        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(ResultError.None, result.Error);

        var stored = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == "mi-tenant");
        Assert.NotNull(stored);
        Assert.Equal("Mi Tenant", stored!.Name);
    }
}
