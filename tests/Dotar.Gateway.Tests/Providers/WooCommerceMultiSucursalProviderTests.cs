using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using GatewayEntities = Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Tests.Providers;

/// <summary>
/// Tests TDD para WooCommerceMultiSucursalProvider.
/// Cubren: RequiereConfigProveedor=false, RutearSinEnriquecimiento=true,
/// ExtraerRoutingKeyConConfig delega en SucursalMetaDataExtractor, EnriquecerAsync=Fallo.
///
/// Fixture de payload: key "_multilocal_pickup_location_id", value "sucursal-godoy-cruz",
/// sin separador → routing key "sucursal-godoy-cruz" (id 171, payload-format confirmado).
/// </summary>
public class WooCommerceMultiSucursalProviderTests
{
    // ─── Payload realista (MultiLocal, id 171) ────────────────────────────────

    private const string PayloadConSucursal = """
        {
          "id": 1234,
          "status": "processing",
          "meta_data": [
            { "key": "_multilocal_pickup_location_id", "value": "sucursal-godoy-cruz" },
            { "key": "_multilocal_pickup_location_name", "value": "Sucursal Godoy Cruz" },
            { "key": "_multilocal_pickup_type", "value": "local_pickup" }
          ]
        }
        """;

    private static WooCommerceMultiSucursalProvider BuildSut()
        => new(NullLogger<WooCommerceMultiSucursalProvider>.Instance);

    private static Tenant BuildTenant(
        string? metaKey = "_multilocal_pickup_location_id",
        string? separador = null)
        => new()
        {
            Id = 1,
            Name = "Tienda Norte",
            Slug = "tienda-norte",
            WebhookSecret = "wc-secret-test",
            TargetUrl = "https://shop.example.com/webhooks",
            IsActive = true,
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal",
            SucursalMetaKey = metaKey,
            SucursalMetaSeparador = separador
        };

    // ─── 1. RequiereConfigProveedor = false ───────────────────────────────────

    [Fact]
    public void RequiereConfigProveedor_RetornaFalse()
    {
        var sut = BuildSut();
        Assert.False(sut.RequiereConfigProveedor);
    }

    // ─── 2. RutearSinEnriquecimiento = true siempre ───────────────────────────

    [Fact]
    public void RutearSinEnriquecimiento_SiempreRetornaTrue()
    {
        var sut = BuildSut();
        Assert.True(sut.RutearSinEnriquecimiento(PayloadConSucursal));
        Assert.True(sut.RutearSinEnriquecimiento("{}"));
        Assert.True(sut.RutearSinEnriquecimiento("payload-invalido-no-json"));
    }

    // ─── 3. ExtraerRoutingKeyConConfig delega en SucursalMetaDataExtractor ────

    [Fact]
    public void ExtraerRoutingKeyConConfig_KeyPresente_SinSeparador_RetornaRoutingKey()
    {
        // GIVEN tenant con metaKey "_multilocal_pickup_location_id" y sin separador
        var sut = BuildSut();
        var tenant = BuildTenant("_multilocal_pickup_location_id", null);

        // WHEN extraemos la routing key
        var resultado = sut.ExtraerRoutingKeyConConfig(PayloadConSucursal, tenant);

        // THEN routing key = "sucursal-godoy-cruz" (sin separador, value completo)
        Assert.True(resultado.EsValido);
        Assert.Equal("sucursal-godoy-cruz", resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKeyConConfig_ConSeparador_RetornaParteIzquierda()
    {
        // GIVEN payload donde el value tiene separador "__"
        var payloadConSeparador = """
            {"meta_data":[{"key":"_pickup_id","value":"sucursal-norte__20260625"}]}
            """;
        var sut = BuildSut();
        var tenant = BuildTenant("_pickup_id", "__");

        var resultado = sut.ExtraerRoutingKeyConConfig(payloadConSeparador, tenant);

        Assert.True(resultado.EsValido);
        Assert.Equal("sucursal-norte", resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKeyConConfig_MetaDataAusente_RetornaInvalido()
    {
        var sut = BuildSut();
        var tenant = BuildTenant("_multilocal_pickup_location_id", null);

        var resultado = sut.ExtraerRoutingKeyConConfig("{}", tenant);

        Assert.False(resultado.EsValido);
    }

    [Fact]
    public void ExtraerRoutingKeyConConfig_KeyNoEncontrada_RetornaInvalido()
    {
        // GIVEN payload con meta_data pero sin la key configurada
        var payload = """{"meta_data":[{"key":"otra_key","value":"otro_valor"}]}""";
        var sut = BuildSut();
        var tenant = BuildTenant("_multilocal_pickup_location_id", null);

        var resultado = sut.ExtraerRoutingKeyConConfig(payload, tenant);

        Assert.False(resultado.EsValido);
    }

    // ─── 4. EnriquecerAsync → Fallo defensivo ─────────────────────────────────

    [Fact]
    public async Task EnriquecerAsync_RetornaFalloDefensivo()
    {
        var sut = BuildSut();
        // WooCommerce no usa ProveedorWebhookConfig — pasamos null-forgiven con un dummy
        var configDummy = new GatewayEntities.ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "woocommerce-multisucursal",
            CuentaExternaId = "n/a",
            CredencialesCifradas = "{}",
            BaseUrl = string.Empty,
            IsActive = true
        };

        var resultado = await sut.EnriquecerAsync("evento-123", configDummy, CancellationToken.None);

        Assert.False(resultado.Exitoso);
        Assert.NotNull(resultado.ErrorMessage);
    }

    // ─── 5. Nombre correcto ───────────────────────────────────────────────────

    [Fact]
    public void Nombre_EsWoocommerceMultisucursal()
    {
        var sut = BuildSut();
        Assert.Equal("woocommerce-multisucursal", sut.Nombre);
    }
}
