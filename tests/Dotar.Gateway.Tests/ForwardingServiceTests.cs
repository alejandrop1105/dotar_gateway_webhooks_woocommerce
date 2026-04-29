using System.Net;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests;

/// <summary>
/// Verifica que ForwardingService propague verbatim los headers del provider
/// al request HTTP saliente. Captura el HttpRequestMessage con un handler mock.
/// </summary>
public class ForwardingServiceTests
{
    [Fact]
    public async Task Forward_Propagates_AllProviderHeaders_Verbatim()
    {
        var captured = new RequestCapturingHandler();
        var sut = BuildSut(captured);

        var headers = new Dictionary<string, string>
        {
            ["X-WC-Webhook-Topic"] = "order.created",
            ["X-WC-Webhook-Event"] = "created",
            ["X-WC-Webhook-Signature"] = "abc123",
            ["X-WC-Webhook-Delivery-ID"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ["X-Original-User-Agent"] = "WooCommerce/8.5; Verifying"
        };

        var result = await sut.ForwardAsync("https://downstream.test/hook", "{}", "tenant-x", headers);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured.Request);

        // Cada header debe estar presente con el mismo nombre (verbatim) y valor exacto.
        AssertHeader(captured.Request!, "X-WC-Webhook-Topic", "order.created");
        AssertHeader(captured.Request!, "X-WC-Webhook-Event", "created");
        AssertHeader(captured.Request!, "X-WC-Webhook-Signature", "abc123");
        AssertHeader(captured.Request!, "X-WC-Webhook-Delivery-ID", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        AssertHeader(captured.Request!, "X-Original-User-Agent", "WooCommerce/8.5; Verifying");

        // X-Dotar-Gateway-ID también va, sin pisar los del provider.
        AssertHeader(captured.Request!, "X-Dotar-Gateway-ID", "tenant-x");
    }

    [Fact]
    public async Task Forward_DoesNotInject_BlacklistedHeaders_EvenIfPresentInDict()
    {
        // Un actor malicioso podría inyectar headers de transporte vía la cola.
        // ForwardingService debe re-aplicar la política como defensa en profundidad.
        var captured = new RequestCapturingHandler();
        var sut = BuildSut(captured);

        var headers = new Dictionary<string, string>
        {
            ["X-WC-Webhook-Topic"] = "order.created",
            ["X-Forwarded-For"] = "evil",
            ["Cf-Connecting-IP"] = "evil",
            ["Host"] = "evil.com",
            ["X-Real-IP"] = "evil"
        };

        await sut.ForwardAsync("https://downstream.test/hook", "{}", "tenant-x", headers);

        Assert.NotNull(captured.Request);
        Assert.True(captured.Request!.Headers.Contains("X-WC-Webhook-Topic"));
        Assert.False(captured.Request.Headers.Contains("X-Forwarded-For"));
        Assert.False(captured.Request.Headers.Contains("Cf-Connecting-IP"));
        Assert.False(captured.Request.Headers.Contains("X-Real-IP"));
        // Host es transport — HttpClient lo regenera siempre desde el URI.
        Assert.NotEqual("evil.com", captured.Request.Headers.Host);
    }

    [Fact]
    public async Task Forward_PreservesExactHeaderCasing()
    {
        var captured = new RequestCapturingHandler();
        var sut = BuildSut(captured);

        var headers = new Dictionary<string, string>
        {
            // Capitalización exacta tal como la envía WooCommerce.
            ["X-WC-Webhook-Topic"] = "order.created"
        };

        await sut.ForwardAsync("https://downstream.test/hook", "{}", "tenant-x", headers);

        Assert.NotNull(captured.Request);
        var actualName = captured.Request!.Headers
            .First(h => string.Equals(h.Key, "X-WC-Webhook-Topic", StringComparison.OrdinalIgnoreCase))
            .Key;
        Assert.Equal("X-WC-Webhook-Topic", actualName);
    }

    [Fact]
    public async Task Forward_NullHeaders_DoesNotThrow()
    {
        var captured = new RequestCapturingHandler();
        var sut = BuildSut(captured);

        var result = await sut.ForwardAsync("https://downstream.test/hook", "{}", "tenant-x", forwardedHeaders: null);

        Assert.True(result.IsSuccess);
        Assert.True(captured.Request!.Headers.Contains("X-Dotar-Gateway-ID"));
    }

    [Fact]
    public async Task Forward_RetainsContentTypeJson()
    {
        var captured = new RequestCapturingHandler();
        var sut = BuildSut(captured);
        await sut.ForwardAsync("https://downstream.test/hook", "{\"x\":1}", "tenant-x");

        Assert.NotNull(captured.Request?.Content);
        Assert.Equal("application/json", captured.Request!.Content!.Headers.ContentType?.MediaType);
    }

    // ───────── helpers ─────────

    private static ForwardingService BuildSut(HttpMessageHandler handler)
    {
        var clientFactory = new SingleClientFactory(new HttpClient(handler));
        return new ForwardingService(clientFactory, NullLogger<ForwardingService>.Instance);
    }

    private static void AssertHeader(HttpRequestMessage req, string name, string expectedValue)
    {
        var inRequest = req.Headers.TryGetValues(name, out var v1) ? string.Join(",", v1) : null;
        var inContent = req.Content?.Headers.TryGetValues(name, out var v2) == true ? string.Join(",", v2!) : null;
        var actual = inRequest ?? inContent;
        Assert.Equal(expectedValue, actual);
    }

    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Materializamos el body para no perderlo si lo vacían tests posteriores.
            if (request.Content is not null) await request.Content.LoadIntoBufferAsync();
            Request = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) { _client = client; }
        public HttpClient CreateClient(string name) => _client;
    }
}
