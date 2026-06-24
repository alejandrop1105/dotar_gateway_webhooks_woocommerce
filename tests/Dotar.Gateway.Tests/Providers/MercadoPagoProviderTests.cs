using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Providers;

/// <summary>
/// Tests TDD para MercadoPagoProvider.
/// Cubren los 4 métodos con casos válidos, inválidos y edge cases.
/// </summary>
public class MercadoPagoProviderTests
{
    // ─── helpers ───────────────────────────────────────────────────────────

    private static MercadoPagoProvider BuildSut(HttpMessageHandler? handler = null)
    {
        var innerHandler = handler ?? new FakeResponseHandler(HttpStatusCode.OK, "{}");
        var client = new HttpClient(innerHandler);
        return new MercadoPagoProvider(client, NullLogger<MercadoPagoProvider>.Instance);
    }

    private static ProveedorWebhookConfig BuildConfig(
        string signingSecret = "mi-secret-mp",
        string accessToken = "APP_USR-token",
        string baseUrl = "https://api.mercadopago.com")
    {
        // CredencialesCifradas almacena JSON con los campos requeridos por el proveedor
        var credenciales = JsonSerializer.Serialize(new
        {
            AccessToken = accessToken,
            SigningSecret = signingSecret
        });

        return new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "123456789",
            CredencialesCifradas = credenciales,
            BaseUrl = baseUrl,
            IsActive = true
        };
    }

    private static IHeaderDictionary BuildHeaders(Dictionary<string, string>? entries = null)
    {
        var headers = new HeaderDictionary();
        if (entries is not null)
            foreach (var (k, v) in entries)
                headers[k] = v;
        return headers;
    }

    /// <summary>
    /// Firma correcta x-signature según la spec oficial de MP:
    /// manifest = "id:{dataId};request-id:{requestId};ts:{ts};"
    /// HMAC-SHA256 hex sobre el manifest con la key = signingSecret.
    /// </summary>
    private static string BuildXSignature(string ts, string v1Hex)
        => $"ts={ts},v1={v1Hex}";

    private static string ComputeHmacHex(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        return Convert.ToHexString(HMACSHA256.HashData(keyBytes, msgBytes)).ToLowerInvariant();
    }

    // ─── ResolverCuentaExterna ─────────────────────────────────────────────

    [Fact]
    public void ResolverCuentaExterna_ConUserId_RetornaUserId()
    {
        var sut = BuildSut();
        var body = Encoding.UTF8.GetBytes(
            "{\"id\":123,\"type\":\"payment\",\"user_id\":987654321,\"data\":{\"id\":\"456\"}}");
        var headers = BuildHeaders();

        var resultado = sut.ResolverCuentaExterna(headers, body);

        Assert.Equal("987654321", resultado);
    }

    [Fact]
    public void ResolverCuentaExterna_SinUserId_RetornaNull()
    {
        var sut = BuildSut();
        var body = Encoding.UTF8.GetBytes("{\"id\":123,\"type\":\"payment\"}");
        var headers = BuildHeaders();

        var resultado = sut.ResolverCuentaExterna(headers, body);

        Assert.Null(resultado);
    }

    [Fact]
    public void ResolverCuentaExterna_BodyMalformado_RetornaNull()
    {
        var sut = BuildSut();
        var body = Encoding.UTF8.GetBytes("no-es-json");
        var headers = BuildHeaders();

        var resultado = sut.ResolverCuentaExterna(headers, body);

        Assert.Null(resultado);
    }

    [Fact]
    public void ResolverCuentaExterna_UserIdComoString_RetornaString()
    {
        // MP puede enviar user_id como número o string según la versión de la API
        var sut = BuildSut();
        var body = Encoding.UTF8.GetBytes("{\"user_id\":\"555666777\",\"data\":{\"id\":\"1\"}}");
        var headers = BuildHeaders();

        var resultado = sut.ResolverCuentaExterna(headers, body);

        Assert.Equal("555666777", resultado);
    }

    // ─── ValidarFirmaEntrante ──────────────────────────────────────────────

    [Fact]
    public void ValidarFirmaEntrante_FirmaValida_RetornaTrue()
    {
        var sut = BuildSut();
        var config = BuildConfig(signingSecret: "test-secret-123");

        var dataId = "456";
        var requestId = "req-abc";
        var ts = "1718000000000";
        // manifest: "id:{dataId};request-id:{requestId};ts:{ts};"
        var manifest = $"id:{dataId};request-id:{requestId};ts:{ts};";
        var v1 = ComputeHmacHex(manifest, "test-secret-123");

        var body = Encoding.UTF8.GetBytes($"{{\"data\":{{\"id\":\"{dataId}\"}}}}");
        var headers = BuildHeaders(new Dictionary<string, string>
        {
            ["x-signature"] = BuildXSignature(ts, v1),
            ["x-request-id"] = requestId
        });

        var resultado = sut.ValidarFirmaEntrante(headers, body, config);

        Assert.True(resultado);
    }

    [Fact]
    public void ValidarFirmaEntrante_FirmaInvalida_RetornaFalse()
    {
        var sut = BuildSut();
        var config = BuildConfig(signingSecret: "test-secret-123");

        var body = Encoding.UTF8.GetBytes("{\"data\":{\"id\":\"456\"}}");
        var headers = BuildHeaders(new Dictionary<string, string>
        {
            ["x-signature"] = "ts=1718000000000,v1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["x-request-id"] = "req-abc"
        });

        var resultado = sut.ValidarFirmaEntrante(headers, body, config);

        Assert.False(resultado);
    }

    [Fact]
    public void ValidarFirmaEntrante_HeaderAusente_RetornaFalse()
    {
        var sut = BuildSut();
        var config = BuildConfig();

        var body = Encoding.UTF8.GetBytes("{\"data\":{\"id\":\"456\"}}");
        var headers = BuildHeaders(); // sin x-signature

        var resultado = sut.ValidarFirmaEntrante(headers, body, config);

        Assert.False(resultado);
    }

    [Fact]
    public void ValidarFirmaEntrante_SoloIdEnManifest_SinRequestId_NiTs_RetornaTrue()
    {
        // Si x-request-id está ausente, se omite del manifest.
        // Si ts está pero request-id no, manifest = "id:{dataId};ts:{ts};"
        var sut = BuildSut();
        var config = BuildConfig(signingSecret: "secreto");

        var dataId = "789";
        var ts = "1718111111111";
        // Sin request-id: manifest = "id:{dataId};ts:{ts};"
        var manifest = $"id:{dataId};ts:{ts};";
        var v1 = ComputeHmacHex(manifest, "secreto");

        var body = Encoding.UTF8.GetBytes($"{{\"data\":{{\"id\":\"{dataId}\"}}}}");
        var headers = BuildHeaders(new Dictionary<string, string>
        {
            ["x-signature"] = BuildXSignature(ts, v1)
            // sin x-request-id
        });

        var resultado = sut.ValidarFirmaEntrante(headers, body, config);

        Assert.True(resultado);
    }

    // ─── ExtraerRoutingKey ─────────────────────────────────────────────────

    [Fact]
    public void ExtraerRoutingKey_ConSeparador_RetornaParteIzquierda()
    {
        var sut = BuildSut();
        var payload = "{\"external_reference\":\"CAJA-01__00001234\"}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.True(resultado.EsValido);
        Assert.Equal("CAJA-01", resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKey_IdentificadorConGuiones_RetornaIdentificadorCompleto()
    {
        var sut = BuildSut();
        var payload = "{\"external_reference\":\"CAJA-ESPECIAL-01__comprobante-999\"}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.True(resultado.EsValido);
        Assert.Equal("CAJA-ESPECIAL-01", resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKey_IdentificadorConGuionBajoSimple_RetornaIdentificadorCompleto()
    {
        // El identificador real del ERP conserva su guion bajo simple: 003-CAJA_2.
        // El separador "__" (doble) no debe partir dentro del identificador.
        var sut = BuildSut();
        var payload = "{\"external_reference\":\"003-CAJA_2__260624095836\"}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.True(resultado.EsValido);
        Assert.Equal("003-CAJA_2", resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKey_SinSeparador_RetornaInvalido()
    {
        var sut = BuildSut();
        var payload = "{\"external_reference\":\"CAJA-01-SIN-SEPARADOR\"}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.False(resultado.EsValido);
        Assert.Null(resultado.RoutingKey);
    }

    [Fact]
    public void ExtraerRoutingKey_ParteIzquierdaVacia_RetornaInvalido()
    {
        var sut = BuildSut();
        var payload = "{\"external_reference\":\"__comprobante\"}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.False(resultado.EsValido);
    }

    [Fact]
    public void ExtraerRoutingKey_CampoAusente_RetornaInvalido()
    {
        var sut = BuildSut();
        var payload = "{\"status\":\"approved\"}"; // sin external_reference

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.False(resultado.EsValido);
    }

    [Fact]
    public void ExtraerRoutingKey_CampoNulo_RetornaInvalido()
    {
        var sut = BuildSut();
        var payload = "{\"external_reference\":null}";

        var resultado = sut.ExtraerRoutingKey(payload);

        Assert.False(resultado.EsValido);
    }

    // ─── EnriquecerAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task EnriquecerAsync_Respuesta200_RetornaPayloadEnriquecido()
    {
        var payloadRespuesta = "{\"id\":456,\"external_reference\":\"CAJA-01__00001\",\"status\":\"approved\"}";
        var handler = new FakeResponseHandler(HttpStatusCode.OK, payloadRespuesta);
        var sut = BuildSut(handler);
        var config = BuildConfig(accessToken: "TEST_TOKEN", baseUrl: "https://api.mercadopago.com");

        var resultado = await sut.EnriquecerAsync("456", config, CancellationToken.None);

        Assert.True(resultado.Exitoso);
        Assert.Equal(payloadRespuesta, resultado.PayloadEnriquecido);
    }

    [Fact]
    public async Task EnriquecerAsync_Respuesta5xx_RetornaFallo()
    {
        var handler = new FakeResponseHandler(HttpStatusCode.InternalServerError, "error del server");
        var sut = BuildSut(handler);
        var config = BuildConfig();

        var resultado = await sut.EnriquecerAsync("789", config, CancellationToken.None);

        Assert.False(resultado.Exitoso);
        Assert.Null(resultado.PayloadEnriquecido);
    }

    [Fact]
    public async Task EnriquecerAsync_UsaUrlCorrecta_ConBearerToken()
    {
        HttpRequestMessage? capturado = null;
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}", r => capturado = r);
        var sut = BuildSut(handler);
        var config = BuildConfig(accessToken: "MI-TOKEN", baseUrl: "https://api.mercadopago.com");

        await sut.EnriquecerAsync("999", config, CancellationToken.None);

        Assert.NotNull(capturado);
        Assert.Equal("https://api.mercadopago.com/v1/payments/999", capturado!.RequestUri!.ToString());
        Assert.Equal("Bearer MI-TOKEN", capturado.Headers.Authorization?.ToString());
    }

    // ─── Handlers fake ─────────────────────────────────────────────────────

    private sealed class FakeResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public FakeResponseHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly Action<HttpRequestMessage> _capture;

        public CapturingHandler(HttpStatusCode statusCode, string body, Action<HttpRequestMessage> capture)
        {
            _statusCode = statusCode;
            _body = body;
            _capture = capture;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _capture(request);
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
