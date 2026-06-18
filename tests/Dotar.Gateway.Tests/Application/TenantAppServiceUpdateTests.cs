using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de TenantAppService.UpdateAsync.
/// Usa GatewayDbContext con SQLite en memoria y un fake de ITenantCacheService.
/// </summary>
public class TenantAppServiceUpdateTests : IDisposable
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

    public TenantAppServiceUpdateTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-update-{Guid.NewGuid():N}.db");
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

    /// <summary>Crea un tenant base directamente en la DB para preparar los tests de update.</summary>
    private async Task<Tenant> CrearTenantBase(
        string slug = "mi-tenant",
        string nombre = "Mi Tenant",
        string targetUrl = "https://destino.com/api/webhooks")
    {
        var tenant = new Tenant
        {
            Name            = nombre,
            Slug            = slug,
            TargetUrl       = targetUrl,
            WebhookSecret   = "secret-base64",
            IsActive        = true,
            SignatureScheme = SignatureScheme.WooCommerce,
            CreatedAt       = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        // Limpiar el cache de invalidaciones de la creación manual
        _cache.InvalidatedSlugs.Clear();
        return tenant;
    }

    // ─── Not Found ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_SlugNoExiste_DevuelveNotFound()
    {
        var input = new UpdateTenantInput(Name: "Nuevo Nombre");
        var result = await _service.UpdateAsync("no-existe", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
    }

    // ─── Validación de TargetUrl ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_TargetUrlVacia_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TargetUrl: "");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_TargetUrlInvalida_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TargetUrl: "no-es-una-url-valida");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_TargetUrlSoloWhitespace_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TargetUrl: "   ");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Edición exitosa ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_DatosValidos_DevuelveIsSuccessYActualizaNombre()
    {
        await CrearTenantBase();
        var antes = DateTime.UtcNow;

        var input = new UpdateTenantInput(Name: "Nombre Actualizado");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Equal(ResultError.None, result.Error);
        Assert.Equal("Nombre Actualizado", result.Value!.Name);
    }

    [Fact]
    public async Task UpdateAsync_DatosValidos_UpdatedAtEsUtcPosteriorAlInicio()
    {
        await CrearTenantBase();
        var antes = DateTime.UtcNow.AddMilliseconds(-50);

        var input = new UpdateTenantInput(Name: "Nombre Nuevo");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.UpdatedAt);
        Assert.True(result.Value!.UpdatedAt!.Value >= antes,
            $"UpdatedAt ({result.Value!.UpdatedAt}) debería ser >= {antes}");
    }

    [Fact]
    public async Task UpdateAsync_ActualizaTargetUrl_DevuelveIsSuccess()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TargetUrl: "https://nueva-url.com/webhooks");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://nueva-url.com/webhooks", result.Value!.TargetUrl);
    }

    [Fact]
    public async Task UpdateAsync_ActualizaSignatureScheme_DevuelveIsSuccess()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(SignatureScheme: SignatureScheme.GitHub);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Equal(SignatureScheme.GitHub, result.Value!.SignatureScheme);
    }

    [Fact]
    public async Task UpdateAsync_ActualizaIsActive_DevuelveIsSuccess()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(IsActive: false);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_ActualizaWebhookSecret_DevuelveIsSuccess()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(WebhookSecret: "nuevo-secret-base64");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Equal("nuevo-secret-base64", result.Value!.WebhookSecret);
    }

    [Fact]
    public async Task UpdateAsync_ActualizaSignatureHeader_DevuelveIsSuccess()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(SignatureHeader: "X-Custom-Sig");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Equal("X-Custom-Sig", result.Value!.SignatureHeader);
    }

    // ─── FKs: 0 → null y FK inválida → Validation ────────────────────────────

    [Fact]
    public async Task UpdateAsync_RetryPolicyIdCero_DesasoicaFK()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(RetryPolicyId: 0);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.RetryPolicyId);
    }

    [Fact]
    public async Task UpdateAsync_TenantGroupIdCero_DesasoicaFK()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TenantGroupId: 0);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.TenantGroupId);
    }

    [Fact]
    public async Task UpdateAsync_RetryPolicyIdInexistente_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(RetryPolicyId: 9999);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_TenantGroupIdInexistente_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(TenantGroupId: 9999);
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Validación de Name vacío ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_NameVacio_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(Name: "");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_NameSoloWhitespace_DevuelveValidation()
    {
        await CrearTenantBase();

        var input = new UpdateTenantInput(Name: "   ");
        var result = await _service.UpdateAsync("mi-tenant", input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── Slug inmutable ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_SlugNoEstaEnInput_SlugPermanecIgual()
    {
        // El contrato de UpdateTenantInput no tiene campo Slug.
        // Este test verifica que el slug no cambia al hacer update.
        await CrearTenantBase(slug: "slug-original");

        var input = new UpdateTenantInput(Name: "Nombre Cambiado");
        var result = await _service.UpdateAsync("slug-original", input);

        Assert.True(result.IsSuccess);
        Assert.Equal("slug-original", result.Value!.Slug);

        // Verificar en DB también
        var enDb = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == "slug-original");
        Assert.NotNull(enDb);
    }

    // ─── Invalidación de caché ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_Exitoso_InvalidaCacheExactamenteUnaVez()
    {
        await CrearTenantBase(slug: "cache-update");

        var input = new UpdateTenantInput(Name: "Nuevo");
        var result = await _service.UpdateAsync("cache-update", input);

        Assert.True(result.IsSuccess);
        Assert.Single(_cache.InvalidatedSlugs);
        Assert.Equal("cache-update", _cache.InvalidatedSlugs[0]);
    }

    [Fact]
    public async Task UpdateAsync_NotFound_NoCacheInvalidada()
    {
        var input = new UpdateTenantInput(Name: "Nuevo");
        await _service.UpdateAsync("no-existe", input);

        Assert.Empty(_cache.InvalidatedSlugs);
    }
}
