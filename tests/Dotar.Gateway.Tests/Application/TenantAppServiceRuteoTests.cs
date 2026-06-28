using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests de WU-1, WU-2 y WU-3 (aplicación): catálogo de proveedores y validación de ruteo en TenantAppService.
/// Usa GatewayDbContext con SQLite temporal, FakeCacheService y FakeProveedorRuteoCatalog.
/// </summary>
public class TenantAppServiceRuteoTests : IDisposable
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeCacheService : ITenantCacheService
    {
        public void Invalidate(string slug) { }
        public Task<Tenant?> GetBySlugAsync(string slug) => Task.FromResult<Tenant?>(null);
    }

    /// <summary>
    /// Catálogo fake con keys fijas: mercadopago y woocommerce-multisucursal.
    /// Permite testear la validación sin depender del contenedor DI real.
    /// </summary>
    private sealed class FakeProveedorRuteoCatalog : IProveedorRuteoCatalog
    {
        public IReadOnlyCollection<string> KeysValidas =>
            new[] { "mercadopago", "woocommerce-multisucursal" };
    }

    // ─── Infraestructura de test ──────────────────────────────────────────────

    private readonly GatewayDbContext _db;
    private readonly TenantAppService _service;

    public TenantAppServiceRuteoTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ruteo-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _service = new TenantAppService(
            _db,
            new FakeCacheService(),
            NullLogger<TenantAppService>.Instance,
            new FakeProveedorRuteoCatalog());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private static CreateTenantInput InputBase(string slug = "mi-tenant") =>
        new(Name: "Test", Slug: slug, TargetUrl: "https://test.com/h");

    private async Task<string> CrearTenantBase(string slug = "base-tenant")
    {
        var input = InputBase(slug);
        var result = await _service.CreateAsync(input);
        Assert.True(result.IsSuccess);
        return slug;
    }

    // ─── WU-1: Smoke test del catálogo ───────────────────────────────────────

    [Fact]
    public void ProveedorRuteoCatalog_KeysValidas_ContieneMercadoPago()
    {
        var catalog = new FakeProveedorRuteoCatalog();
        Assert.Contains("mercadopago", catalog.KeysValidas);
    }

    [Fact]
    public void ProveedorRuteoCatalog_KeysValidas_ContieneWooCommerceMultiSucursal()
    {
        var catalog = new FakeProveedorRuteoCatalog();
        Assert.Contains("woocommerce-multisucursal", catalog.KeysValidas);
    }

    // ─── WU-2: Crear sin campos de ruteo — default false, compat hacia atrás ─

    [Fact]
    public async Task CreateAsync_SinCamposRuteo_RuteoActivoFalse()
    {
        var result = await _service.CreateAsync(InputBase());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.RuteoProveedorActivo);
    }

    [Fact]
    public async Task CreateAsync_SinCamposRuteo_CamposDependientesVacios()
    {
        var result = await _service.CreateAsync(InputBase());

        Assert.True(result.IsSuccess);
        var t = result.Value!;
        // Campos opcionales nulos o vacíos cuando el ruteo está inactivo.
        Assert.True(string.IsNullOrEmpty(t.ProveedorRuteoNombre));
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaKey));
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaSeparador));
    }

    // ─── WU-2: Validación — activar ruteo sin ProveedorRuteoNombre ───────────

    [Fact]
    public async Task CreateAsync_RuteoActivoSinProveedorNombre_DevuelveValidation()
    {
        var input = InputBase("slug-sin-proveedor") with
        {
            RuteoProveedorActivo = true,
            SucursalMetaKey = "_woosea_item_id"
        };

        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("ProveedorRuteoNombre", result.Message ?? string.Empty);
    }

    // ─── WU-2: Validación — activar ruteo sin SucursalMetaKey ───────────────

    [Fact]
    public async Task CreateAsync_RuteoActivoSinSucursalMetaKey_DevuelveValidation()
    {
        var input = InputBase("slug-sin-metakey") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal"
        };

        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("SucursalMetaKey", result.Message ?? string.Empty);
    }

    // ─── WU-2: Validación — ProveedorRuteoNombre inválido ───────────────────

    [Fact]
    public async Task CreateAsync_ProveedorNombreInvalido_DevuelveValidation()
    {
        var input = InputBase("slug-proveedor-invalido") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "paypal",
            SucursalMetaKey = "_id"
        };

        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("paypal", result.Message ?? string.Empty);
    }

    // ─── WU-2: Happy path — crear con ruteo completo ─────────────────────────

    [Fact]
    public async Task CreateAsync_RuteoCompletoValido_PersisteCampos()
    {
        var input = InputBase("slug-ruteo-ok") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal",
            SucursalMetaKey = "_woosea_item_id",
            SucursalMetaSeparador = ":"
        };

        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        var t = result.Value!;
        Assert.True(t.RuteoProveedorActivo);
        Assert.Equal("woocommerce-multisucursal", t.ProveedorRuteoNombre);
        Assert.Equal("_woosea_item_id", t.SucursalMetaKey);
        Assert.Equal(":", t.SucursalMetaSeparador);
    }

    // ─── WU-2: Update parcial — campo nulo no toca ruteo ─────────────────────

    [Fact]
    public async Task UpdateAsync_SinCamposRuteo_NoCambiaNadaDeRuteo()
    {
        // Crear con ruteo activo
        var createInput = InputBase("slug-update-nochange") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal",
            SucursalMetaKey = "_woosea_item_id"
        };
        await _service.CreateAsync(createInput);

        // Update solo de nombre — sin campos de ruteo → null = sin cambio
        var updateInput = new UpdateTenantInput(Name: "Nuevo Nombre");
        var result = await _service.UpdateAsync("slug-update-nochange", updateInput);

        Assert.True(result.IsSuccess);
        var t = result.Value!;
        Assert.True(t.RuteoProveedorActivo, "RuteoProveedorActivo no debe cambiar");
        Assert.Equal("woocommerce-multisucursal", t.ProveedorRuteoNombre);
        Assert.Equal("_woosea_item_id", t.SucursalMetaKey);
    }

    // ─── WU-2: Apagado — RuteoProveedorActivo=false limpia dependientes ──────

    [Fact]
    public async Task UpdateAsync_DesactivarRuteo_LimpiaCamposDependientes()
    {
        // Crear con ruteo activo
        var createInput = InputBase("slug-apagado") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal",
            SucursalMetaKey = "_woosea_item_id",
            SucursalMetaSeparador = ":"
        };
        await _service.CreateAsync(createInput);

        // Apagar el ruteo sin enviar los dependientes
        var updateInput = new UpdateTenantInput(RuteoProveedorActivo: false);
        var result = await _service.UpdateAsync("slug-apagado", updateInput);

        Assert.True(result.IsSuccess);
        var t = result.Value!;
        Assert.False(t.RuteoProveedorActivo);
        Assert.True(string.IsNullOrEmpty(t.ProveedorRuteoNombre), "ProveedorRuteoNombre debe limpiarse");
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaKey), "SucursalMetaKey debe limpiarse");
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaSeparador), "SucursalMetaSeparador debe limpiarse");
    }

    // ─── WU-2: Update — activar ruteo sin ProveedorRuteoNombre (estado efectivo)

    [Fact]
    public async Task UpdateAsync_RuteoActivoSinProveedorNombre_DevuelveValidation()
    {
        await CrearTenantBase("slug-update-validation");

        // Activar sin proveedorNombre (null = no se envía = sin cambio → efectivo queda vacío)
        var updateInput = new UpdateTenantInput(RuteoProveedorActivo: true);
        var result = await _service.UpdateAsync("slug-update-validation", updateInput);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    // ─── WU-2: Update — proveedor inválido en update ─────────────────────────

    [Fact]
    public async Task UpdateAsync_ProveedorNombreInvalido_DevuelveValidation()
    {
        await CrearTenantBase("slug-update-bad-proveedor");

        var updateInput = new UpdateTenantInput(
            RuteoProveedorActivo: true,
            ProveedorRuteoNombre: "proveedor-inexistente",
            SucursalMetaKey: "_id");
        var result = await _service.UpdateAsync("slug-update-bad-proveedor", updateInput);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("proveedor-inexistente", result.Message ?? string.Empty);
    }

    // ─── WU-2: Update — activar ruteo enviando los dependientes en el mismo call ──

    [Fact]
    public async Task UpdateAsync_ActivarRuteoConDependientesEnElMismoCall_EsExitoso()
    {
        // Crear sin campos de ruteo (los dependientes quedan limpios porque ruteo está inactivo).
        var createInput = InputBase("slug-estado-efectivo");
        await _service.CreateAsync(createInput);

        // Activar ruteo incluyendo los dependientes en el mismo UpdateInput.
        var updateInput = new UpdateTenantInput(
            RuteoProveedorActivo: true,
            ProveedorRuteoNombre: "woocommerce-multisucursal",
            SucursalMetaKey: "_woosea_item_id");
        var result = await _service.UpdateAsync("slug-estado-efectivo", updateInput);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.RuteoProveedorActivo);
        Assert.Equal("woocommerce-multisucursal", result.Value!.ProveedorRuteoNombre);
        Assert.Equal("_woosea_item_id", result.Value!.SucursalMetaKey);
    }

    // ─── FIX 1 (C1): catálogo case-sensitive — casing incorrecto es rechazado ─

    [Fact]
    public async Task CreateAsync_ProveedorNombreConCasingIncorrecto_DevuelveValidation()
    {
        // "MercadoPago" tiene casing incorrecto — la key registrada en DI es "mercadopago".
        // El catálogo debe ser case-sensitive para reflejar exactamente lo que el DI resuelve.
        var input = InputBase("slug-casing-incorrecto") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "MercadoPago",
            SucursalMetaKey = "_meta_key"
        };

        var result = await _service.CreateAsync(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("MercadoPago", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task CreateAsync_ProveedorNombreExactoLowercase_EsAceptado()
    {
        // "mercadopago" (minúsculas) coincide exactamente con la key del DI.
        var input = InputBase("slug-casing-correcto") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "mercadopago",
            SucursalMetaKey = "_meta_key"
        };

        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("mercadopago", result.Value!.ProveedorRuteoNombre);
    }

    // ─── FIX 2 (W1): CreateAsync con ruteo inactivo limpia los dependientes ───

    [Fact]
    public async Task CreateAsync_RuteoInactivoConDependientesCargados_PersisteCamposLimpios()
    {
        // Con RuteoProveedorActivo=false, los 3 dependientes deben persistirse vacíos/null
        // aunque se hayan enviado con valores (misma semántica que UpdateAsync).
        var input = InputBase("slug-create-inactivo") with
        {
            RuteoProveedorActivo = false,
            ProveedorRuteoNombre = "mercadopago",
            SucursalMetaKey = "_meta_key",
            SucursalMetaSeparador = ":"
        };

        var result = await _service.CreateAsync(input);

        Assert.True(result.IsSuccess);
        var t = result.Value!;
        Assert.False(t.RuteoProveedorActivo);
        Assert.True(string.IsNullOrEmpty(t.ProveedorRuteoNombre),
            "ProveedorRuteoNombre debe ser null/vacío cuando el ruteo está inactivo");
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaKey),
            "SucursalMetaKey debe ser null/vacío cuando el ruteo está inactivo");
        Assert.True(string.IsNullOrEmpty(t.SucursalMetaSeparador),
            "SucursalMetaSeparador debe ser null/vacío cuando el ruteo está inactivo");
    }

    // ─── FIX 3 (W4/W2): vaciado de dependiente obligatorio en update ─────────

    [Fact]
    public async Task UpdateAsync_VaciarProveedorNombreConRuteoActivo_DevuelveValidation()
    {
        // Crear un tenant con ruteo activo y completo.
        var createInput = InputBase("slug-vaciar-proveedor") with
        {
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "mercadopago",
            SucursalMetaKey = "_meta_key"
        };
        await _service.CreateAsync(createInput);

        // Intentar vaciar ProveedorRuteoNombre con ruteo aún activo → debe ser rechazado.
        var updateInput = new UpdateTenantInput(
            RuteoProveedorActivo: true,
            ProveedorRuteoNombre: "",
            SucursalMetaKey: "_meta_key");
        var result = await _service.UpdateAsync("slug-vaciar-proveedor", updateInput);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Contains("ProveedorRuteoNombre", result.Message ?? string.Empty);
    }
}
