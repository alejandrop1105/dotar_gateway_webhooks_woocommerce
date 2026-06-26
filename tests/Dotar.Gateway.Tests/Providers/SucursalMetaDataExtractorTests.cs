using Dotar.Gateway.Providers;

namespace Dotar.Gateway.Tests.Providers;

/// <summary>
/// Tests unitarios TDD para SucursalMetaDataExtractor.
/// Cubren los 8 casos del diseño: key presente (con/sin separador), value vacío,
/// key ausente, meta_data ausente/no-array, JSON inválido, y separador configurado
/// pero value sin ese separador (retorna value completo).
/// </summary>
public class SucursalMetaDataExtractorTests
{
    // ─── Caso 1: key presente sin separador → retorna value completo ──────────

    [Fact]
    public void Extraer_KeyPresenteSinSeparador_RetornaValueCompleto()
    {
        // GIVEN payload con meta_data que tiene la key buscada y separador null
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"SUC-NORTE"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.True(resultado.EsValido);
        Assert.Equal("SUC-NORTE", resultado.RoutingKey);
    }

    // ─── Caso 2: key con separador "__" y value "003__extra" → "003" ─────────

    [Fact]
    public void Extraer_KeyConSeparadorDobleGuion_RetornaParteIzquierda()
    {
        // GIVEN tenant con separador "__" y value "003__20260625"
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"003__20260625"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", "__");

        Assert.True(resultado.EsValido);
        Assert.Equal("003", resultado.RoutingKey);
    }

    // ─── Caso 3: value vacío → Invalid ───────────────────────────────────────

    [Fact]
    public void Extraer_ValueVacio_RetornaInvalido()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":""}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
        Assert.Null(resultado.RoutingKey);
    }

    // ─── Caso 4: key ausente en meta_data → Invalid ───────────────────────────

    [Fact]
    public void Extraer_KeyAusente_RetornaInvalido()
    {
        var payload = """{"meta_data":[{"key":"otra_key","value":"ALGO"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
        Assert.Null(resultado.RoutingKey);
    }

    // ─── Caso 5a: meta_data ausente → Invalid ─────────────────────────────────

    [Fact]
    public void Extraer_MetaDataAusente_RetornaInvalido()
    {
        var payload = """{"id":1,"status":"completed"}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
    }

    // ─── Caso 5b: meta_data no es array (es objeto) → Invalid ────────────────

    [Fact]
    public void Extraer_MetaDataNoEsArray_RetornaInvalido()
    {
        var payload = """{"meta_data":{"key":"sucursal_codigo","value":"SUC-NORTE"}}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
    }

    // ─── Caso 6: JSON inválido → Invalid ─────────────────────────────────────

    [Fact]
    public void Extraer_JsonInvalido_RetornaInvalido_SinExcepcion()
    {
        var payload = "no-es-json-valido{{{";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
        Assert.Null(resultado.RoutingKey);
    }

    // ─── Caso 7: value con guiones pero sin separador → retorna value entero ──

    [Fact]
    public void Extraer_ValueConGuionesSinSeparador_RetornaValueCompleto()
    {
        // Sin separador configurado: "SUC-NORTE-01" debe retornar tal cual
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"SUC-NORTE-01"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.True(resultado.EsValido);
        Assert.Equal("SUC-NORTE-01", resultado.RoutingKey);
    }

    // ─── Caso 8: separador configurado pero value sin ese separador → value completo

    [Fact]
    public void Extraer_SeparadorConfiguradoPeroValueSinSeparador_RetornaValueCompleto()
    {
        // Separador configurado "__" pero el value no lo tiene → Split da 1 elemento → value completo
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"SUC-NORTE"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", "__");

        Assert.True(resultado.EsValido);
        Assert.Equal("SUC-NORTE", resultado.RoutingKey);
    }

    // ─── Triangulación extra: value con solo espacios → Invalid ──────────────

    [Fact]
    public void Extraer_ValueSoloEspacios_RetornaInvalido()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"   "}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
    }

    // ─── Caso 9: value numérico → se acepta como string ("3") ─────────────────
    // El plugin puede inyectar el código de sucursal como número en vez de string.

    [Fact]
    public void Extraer_ValueNumerico_RetornaComoString()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":3}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.True(resultado.EsValido);
        Assert.Equal("3", resultado.RoutingKey);
    }

    // ─── Caso 10: value numérico con separador configurado ───────────────────

    [Fact]
    public void Extraer_ValueNumericoConSeparador_RetornaParteIzquierda()
    {
        // Número sin el separador → Split da 1 elemento → el número completo.
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":42}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", "__");

        Assert.True(resultado.EsValido);
        Assert.Equal("42", resultado.RoutingKey);
    }

    // ─── Caso 11: value booleano → Invalid (no es un código de sucursal) ──────

    [Fact]
    public void Extraer_ValueBooleano_RetornaInvalido()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":true}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
    }

    // ─── Caso 12: value JSON null explícito → Invalid ────────────────────────

    [Fact]
    public void Extraer_ValueNullExplicito_RetornaInvalido()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":null}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.False(resultado.EsValido);
    }

    // ─── Caso 13: keys duplicadas → gana el primer match ─────────────────────

    [Fact]
    public void Extraer_KeysDuplicadas_RetornaPrimerMatch()
    {
        var payload = """{"meta_data":[{"key":"sucursal_codigo","value":"PRIMERA"},{"key":"sucursal_codigo","value":"SEGUNDA"}]}""";

        var resultado = SucursalMetaDataExtractor.Extraer(payload, "sucursal_codigo", null);

        Assert.True(resultado.EsValido);
        Assert.Equal("PRIMERA", resultado.RoutingKey);
    }
}
