using Dotar.Gateway.Infrastructure.Services;

namespace Dotar.Gateway.Tests;

public class HeaderForwardingPolicyTests
{
    // ───────── ShouldForward ─────────

    [Theory]
    // WooCommerce — todos los del caso concreto deben pasar
    [InlineData("X-WC-Webhook-Topic")]
    [InlineData("X-WC-Webhook-Resource")]
    [InlineData("X-WC-Webhook-Event")]
    [InlineData("X-WC-Webhook-Signature")]
    [InlineData("X-WC-Webhook-Delivery-ID")]
    [InlineData("X-WC-Webhook-Source")]
    // MercadoPago, VTEX, custom
    [InlineData("X-Signature")]
    [InlineData("X-Request-Id")]
    [InlineData("X-Webhook-Secret")]
    [InlineData("X-Api-Key")]
    [InlineData("X-Hub-Signature-256")]
    public void ShouldForward_AcceptsProviderHeaders(string name)
    {
        Assert.True(HeaderForwardingPolicy.ShouldForward(name));
    }

    [Theory]
    // Proxy / transport / Cloudflare
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-Port")]
    [InlineData("X-Real-IP")]
    [InlineData("Cf-Ray")]
    [InlineData("Cf-Connecting-IP")]
    [InlineData("Cf-Visitor")]
    [InlineData("Cdn-Loop")]
    [InlineData("Forwarded")]
    // HTTP transport (no son X- pero igual blacklisted)
    [InlineData("Host")]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Content-Type")]
    [InlineData("Transfer-Encoding")]
    // No X-
    [InlineData("Accept")]
    [InlineData("Authorization")]
    [InlineData("User-Agent")]
    public void ShouldForward_RejectsTransportAndProxyHeaders(string name)
    {
        Assert.False(HeaderForwardingPolicy.ShouldForward(name));
    }

    [Fact]
    public void ShouldForward_IsCaseInsensitive()
    {
        // El downstream puede llegar con cualquier casing por proxy intermedios.
        Assert.True(HeaderForwardingPolicy.ShouldForward("x-wc-webhook-topic"));
        Assert.True(HeaderForwardingPolicy.ShouldForward("X-WC-WEBHOOK-TOPIC"));
        Assert.False(HeaderForwardingPolicy.ShouldForward("x-forwarded-for"));
        Assert.False(HeaderForwardingPolicy.ShouldForward("CF-RAY"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldForward_RejectsBlankNames(string name)
    {
        Assert.False(HeaderForwardingPolicy.ShouldForward(name));
    }

    // ───────── SelectForwardable ─────────

    [Fact]
    public void SelectForwardable_WooCommerceCase_PropagatesAllProviderHeaders()
    {
        // Caso concreto del requerimiento.
        var incoming = new[]
        {
            Kv("X-WC-Webhook-Topic", "order.created"),
            Kv("X-WC-Webhook-Resource", "order"),
            Kv("X-WC-Webhook-Event", "created"),
            Kv("X-WC-Webhook-Signature", "abc123"),
            Kv("X-WC-Webhook-Delivery-ID", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Kv("X-WC-Webhook-Source", "https://shop.example.com"),
            // ruido que NO debe pasar
            Kv("X-Forwarded-For", "1.2.3.4"),
            Kv("Cf-Ray", "abc"),
            Kv("Host", "shop.example.com"),
        };

        var result = HeaderForwardingPolicy.SelectForwardable(incoming, originalUserAgent: "WooCommerce/8.5");

        Assert.Equal("order.created", result["X-WC-Webhook-Topic"]);
        Assert.Equal("order", result["X-WC-Webhook-Resource"]);
        Assert.Equal("created", result["X-WC-Webhook-Event"]);
        Assert.Equal("abc123", result["X-WC-Webhook-Signature"]);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", result["X-WC-Webhook-Delivery-ID"]);
        Assert.Equal("https://shop.example.com", result["X-WC-Webhook-Source"]);
        Assert.Equal("WooCommerce/8.5", result["X-Original-User-Agent"]);

        Assert.False(result.ContainsKey("X-Forwarded-For"));
        Assert.False(result.ContainsKey("Cf-Ray"));
        Assert.False(result.ContainsKey("Host"));
    }

    [Fact]
    public void SelectForwardable_PreservesExactCasing()
    {
        // El nombre debe quedar tal cual entró (no Http-X-Wc-Webhook-Topic ni X_WC_*).
        var incoming = new[] { Kv("X-WC-Webhook-Topic", "order.created") };
        var result = HeaderForwardingPolicy.SelectForwardable(incoming);

        // Tomamos el key real del dict (no usamos lookup case-insensitive aquí)
        var actualKey = result.Keys.Single();
        Assert.Equal("X-WC-Webhook-Topic", actualKey);
    }

    [Fact]
    public void SelectForwardable_MultiValueHeaders_JoinedByComma()
    {
        var incoming = new[]
        {
            new KeyValuePair<string, string[]>("X-Custom", ["a", "b", "c"])
        };
        var result = HeaderForwardingPolicy.SelectForwardable(incoming);
        Assert.Equal("a,b,c", result["X-Custom"]);
    }

    [Fact]
    public void SelectForwardable_NoUserAgent_DoesNotEmitOriginalUserAgent()
    {
        var incoming = new[] { Kv("X-Foo", "bar") };
        var result = HeaderForwardingPolicy.SelectForwardable(incoming);
        Assert.False(result.ContainsKey("X-Original-User-Agent"));
    }

    [Fact]
    public void SelectForwardable_EmptyValues_AreSkipped()
    {
        var incoming = new[]
        {
            new KeyValuePair<string, string[]>("X-Empty", []),
            Kv("X-OK", "yes"),
        };
        var result = HeaderForwardingPolicy.SelectForwardable(incoming);
        Assert.False(result.ContainsKey("X-Empty"));
        Assert.Equal("yes", result["X-OK"]);
    }

    private static KeyValuePair<string, string[]> Kv(string k, string v) => new(k, [v]);
}
