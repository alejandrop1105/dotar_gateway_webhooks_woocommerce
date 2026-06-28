using System.Net;
using System.Net.Http.Json;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dotar.Gateway.Tests;

/// <summary>
/// Tests de integración HTTP para los campos de ruteo en TenantApiEndpoints.
/// Cubre WU-3: Create/Update con campos de ruteo, validaciones 400, compat hacia atrás.
/// </summary>
public class TenantApiEndpointsRuteoTests : IClassFixture<GatewayWebApplicationFactory>
{
    private readonly GatewayWebApplicationFactory _factory;

    public TenantApiEndpointsRuteoTests(GatewayWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private string ApiKey => _factory.Services.GetRequiredService<ApiKeyService>().GetCurrent()!;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, ApiKey);
        return client;
    }

    private string UniqueSlug(string prefix = "ruteo") =>
        $"{prefix}-{Guid.NewGuid():N}".Substring(0, 30);

    // ─── WU-3: Create con campos de ruteo completos ───────────────────────────

    [Fact]
    public async Task Post_CreateConRuteoCompleto_Returns201YCamposEnRespuesta()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("cr-ruteo");

        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Test Ruteo",
            slug,
            targetUrl = "https://test.com/h",
            ruteoProveedorActivo = true,
            proveedorRuteoNombre = "woocommerce-multisucursal",
            sucursalMetaKey = "_woosea_item_id",
            sucursalMetaSeparador = ":"
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        Assert.True(body!.RuteoProveedorActivo);
        Assert.Equal("woocommerce-multisucursal", body.ProveedorRuteoNombre);
        Assert.Equal("_woosea_item_id", body.SucursalMetaKey);
        Assert.Equal(":", body.SucursalMetaSeparador);
    }

    // ─── WU-3: Create SIN campos de ruteo — compat hacia atrás ───────────────

    [Fact]
    public async Task Post_CreateSinCamposRuteo_Returns201YRuteoFalse()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("cr-noruteo");

        // Payload idéntico al que el ERP envía hoy (sin ningún campo de ruteo)
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Sin Ruteo",
            slug,
            targetUrl = "https://test.com/h"
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        Assert.False(body!.RuteoProveedorActivo);
    }

    // ─── WU-3: Create con ruteo activo sin ProveedorRuteoNombre → 400 ─────────

    [Fact]
    public async Task Post_RuteoActivoSinProveedorNombre_Returns400()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("cr-no-prov");

        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X",
            slug,
            targetUrl = "https://test.com/h",
            ruteoProveedorActivo = true,
            sucursalMetaKey = "_woosea_item_id"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ─── WU-3: Create con proveedor inválido → 400 ────────────────────────────

    [Fact]
    public async Task Post_ProveedorNombreInvalido_Returns400()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("cr-bad-prov");

        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X",
            slug,
            targetUrl = "https://test.com/h",
            ruteoProveedorActivo = true,
            proveedorRuteoNombre = "paypal",
            sucursalMetaKey = "_id"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ─── WU-3: Update parcial con campos de ruteo ────────────────────────────

    [Fact]
    public async Task Put_UpdateParcialConRuteo_Returns200YCamposActualizados()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("put-ruteo");

        // Crear tenant sin ruteo
        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Base", slug, targetUrl = "https://test.com/h"
        });

        // Activar ruteo con update parcial
        var resp = await client.PutAsJsonAsync($"/api/tenants/{slug}", new
        {
            ruteoProveedorActivo = true,
            proveedorRuteoNombre = "woocommerce-multisucursal",
            sucursalMetaKey = "_woosea_item_id"
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        Assert.True(body!.RuteoProveedorActivo);
        Assert.Equal("woocommerce-multisucursal", body.ProveedorRuteoNombre);
        Assert.Equal("_woosea_item_id", body.SucursalMetaKey);
    }

    // ─── WU-3: Update — apagar ruteo limpia dependientes ─────────────────────

    [Fact]
    public async Task Put_ApagarRuteo_LimpiaDependientes()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("put-apagar");

        // Crear con ruteo activo
        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Con Ruteo",
            slug,
            targetUrl = "https://test.com/h",
            ruteoProveedorActivo = true,
            proveedorRuteoNombre = "woocommerce-multisucursal",
            sucursalMetaKey = "_woosea_item_id"
        });

        // Apagar ruteo
        var resp = await client.PutAsJsonAsync($"/api/tenants/{slug}", new
        {
            ruteoProveedorActivo = false
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        Assert.False(body!.RuteoProveedorActivo);
        Assert.True(string.IsNullOrEmpty(body.ProveedorRuteoNombre));
        Assert.True(string.IsNullOrEmpty(body.SucursalMetaKey));
    }

    // ─── WU-3: Update ruteo activo sin provider → 400 ────────────────────────

    [Fact]
    public async Task Put_RuteoActivoSinProveedorNombre_Returns400()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("put-no-prov");

        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug, targetUrl = "https://test.com/h"
        });

        var resp = await client.PutAsJsonAsync($"/api/tenants/{slug}", new
        {
            ruteoProveedorActivo = true
            // Sin proveedorRuteoNombre ni sucursalMetaKey
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ─── WU-3: GET incluye campos de ruteo ───────────────────────────────────

    [Fact]
    public async Task Get_TenantConRuteo_IncluyeCamposEnRespuesta()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("get-ruteo");

        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Get Ruteo",
            slug,
            targetUrl = "https://test.com/h",
            ruteoProveedorActivo = true,
            proveedorRuteoNombre = "woocommerce-multisucursal",
            sucursalMetaKey = "_woosea_item_id"
        });

        var resp = await client.GetAsync($"/api/tenants/{slug}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        Assert.True(body!.RuteoProveedorActivo);
        Assert.Equal("woocommerce-multisucursal", body.ProveedorRuteoNombre);
    }

    // ─── WU-3: GET sin ruteo incluye campos con defaults ─────────────────────

    [Fact]
    public async Task Get_TenantSinRuteo_IncluyeCamposConDefaults()
    {
        var client = AuthedClient();
        var slug = UniqueSlug("get-noruteo");

        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Sin Ruteo 2", slug, targetUrl = "https://test.com/h"
        });

        var resp = await client.GetAsync($"/api/tenants/{slug}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TenantConRuteoResponse>();
        Assert.NotNull(body);
        // Los campos deben estar presentes (no omitidos) con sus defaults
        Assert.False(body!.RuteoProveedorActivo);
    }

    // ─── DTOs para deserializar respuestas con campos de ruteo ───────────────

    private record TenantConRuteoResponse(
        string? Slug,
        string? Name,
        string? TargetUrl,
        bool RuteoProveedorActivo,
        string? ProveedorRuteoNombre,
        string? SucursalMetaKey,
        string? SucursalMetaSeparador);
}
