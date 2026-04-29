using System.Net;
using System.Net.Http.Headers;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests;

/// <summary>
/// Test de aceptación: un POST /ingest con headers de WooCommerce debe
/// resultar en un QueuedWebhook con ESOS mismos headers (mismo nombre y valor)
/// listos para ser propagados verbatim al downstream.
/// </summary>
public class IngestHeaderPropagationTests : IClassFixture<IngestHeaderPropagationTests.Factory>
{
    private readonly Factory _factory;

    public IngestHeaderPropagationTests(Factory factory) { _factory = factory; }

    [Fact]
    public async Task Post_Ingest_With_WooCommerce_Headers_PropagatesAll_Verbatim()
    {
        // Arrange — crear un tenant en la DB con SignatureScheme.None para ahorrarnos firmar.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.Tenants.Add(new Tenant
            {
                Name = "WC Shop",
                Slug = "wc-shop",
                WebhookSecret = "irrelevante",
                TargetUrl = "https://downstream.test/hook",
                IsActive = true,
                SignatureScheme = SignatureScheme.None
            });
            await db.SaveChangesAsync();
        }

        // Act
        var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/wc-shop")
        {
            Content = new StringContent("{\"id\":1}", System.Text.Encoding.UTF8, "application/json")
        };

        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Topic", "order.created");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Resource", "order");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Event", "created");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Signature", "abc123");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Delivery-ID", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Source", "https://shop.example.com");
        req.Headers.TryAddWithoutValidation("User-Agent", "WooCommerce/8.5; Verifying");
        // Headers que NO deben propagarse:
        req.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
        req.Headers.TryAddWithoutValidation("Cf-Ray", "abc123-EZE");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Assert — el queue interceptor capturó el QueuedWebhook con los headers correctos.
        var captured = _factory.CapturingQueue.LastEnqueued;
        Assert.NotNull(captured);

        var headers = captured!.ForwardedHeaders;
        Assert.Equal("order.created", headers["X-WC-Webhook-Topic"]);
        Assert.Equal("order", headers["X-WC-Webhook-Resource"]);
        Assert.Equal("created", headers["X-WC-Webhook-Event"]);
        Assert.Equal("abc123", headers["X-WC-Webhook-Signature"]);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", headers["X-WC-Webhook-Delivery-ID"]);
        Assert.Equal("https://shop.example.com", headers["X-WC-Webhook-Source"]);
        Assert.Equal("WooCommerce/8.5; Verifying", headers[HeaderForwardingPolicy.OriginalUserAgentHeader]);

        // Y los proxy/CDN se quedaron afuera.
        Assert.False(headers.ContainsKey("X-Forwarded-For"));
        Assert.False(headers.ContainsKey("Cf-Ray"));
    }

    // ───────── infra ─────────

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"gateway-ingest-{Guid.NewGuid():N}.db");
        public CapturingQueueService CapturingQueue { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Sqlite"] = $"Data Source={DbPath}",
                    ["ConnectionStrings:Redis"] = "localhost:0"
                });
            });
            builder.ConfigureServices(services =>
            {
                // Quitar hosted services
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(d);
                // Reemplazar la cola por un capturing fake
                services.RemoveAll<RedisQueueService>();
                services.AddSingleton(CapturingQueue);
                services.AddSingleton<RedisQueueService>(sp => sp.GetRequiredService<CapturingQueueService>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    public sealed class CapturingQueueService : RedisQueueService
    {
        public QueuedWebhook? LastEnqueued { get; private set; }

        public CapturingQueueService()
            : base(redis: null, new ConfigurationBuilder().Build(), NullLogger<RedisQueueService>.Instance) { }

        public override Task EnqueueAsync(QueuedWebhook webhook)
        {
            LastEnqueued = webhook;
            return Task.CompletedTask;
        }
    }
}
