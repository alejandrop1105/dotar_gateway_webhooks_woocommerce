using System.Net;
using System.Security.Cryptography;
using System.Text;
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

namespace Dotar.Gateway.Tests.Endpoints;

/// <summary>
/// Tests de integración del endpoint POST /ingest/{slug} para tenants WooCommerce multi-sucursal.
///
/// WE-1: RuteoProveedorActivo=true → QueuedWebhook encolado con ProveedorNombre="woocommerce-multisucursal".
/// WE-2: RuteoProveedorActivo=false → QueuedWebhook encolado con ProveedorNombre=null (flujo 1-a-1 intacto).
/// </summary>
public class IngestEndpointsWooCommerceTests
    : IClassFixture<IngestEndpointsWooCommerceTests.IngestWooFactory>
{
    private readonly IngestWooFactory _factory;

    public IngestEndpointsWooCommerceTests(IngestWooFactory factory) { _factory = factory; }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Calcula HMAC-SHA256 en base64 (esquema WooCommerce).</summary>
    private static string ComputarHmacBase64(string secret, byte[] body)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(secretBytes, body);
        return Convert.ToBase64String(hash);
    }

    private HttpClient BuildClient() => _factory.CreateClient();

    // ─── WE-1: RuteoProveedorActivo=true → ProveedorNombre seteado ────────────

    [Fact]
    public async Task Ingest_RuteoProveedorActivo_EncolaConProveedorNombre()
    {
        // Arrange: tenant con RuteoProveedorActivo=true
        string tenantSlug;
        string secret;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var tenant = new Tenant
            {
                Name = "Tienda WooCommerce Multi",
                Slug = "tienda-wc-multi",
                TargetUrl = "https://shop.example.com/webhooks",
                WebhookSecret = "wc-test-secret-multisucursal",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                RuteoProveedorActivo = true,
                ProveedorRuteoNombre = "woocommerce-multisucursal",
                SucursalMetaKey = "_multilocal_pickup_location_id",
                SucursalMetaSeparador = null,
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantSlug = tenant.Slug;
            secret = tenant.WebhookSecret;
        }

        var payload = """{"id":1234,"status":"processing","meta_data":[{"key":"_multilocal_pickup_location_id","value":"sucursal-godoy-cruz"}]}""";
        var body = Encoding.UTF8.GetBytes(payload);
        var signature = ComputarHmacBase64(secret, body);

        var client = BuildClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ingest/{tenantSlug}")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-WC-Webhook-Signature", signature);

        // Act
        var response = await client.SendAsync(request);

        // Assert: 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // El webhook encolado debe tener ProveedorNombre = "woocommerce-multisucursal"
        var encolado = _factory.CapturingQueue.LastEnqueued;
        Assert.NotNull(encolado);
        Assert.Equal("woocommerce-multisucursal", encolado!.ProveedorNombre);
    }

    // ─── WE-2: RuteoProveedorActivo=false → ProveedorNombre=null (flujo 1-a-1) ─

    [Fact]
    public async Task Ingest_RuteoProveedorInactivo_EncolaConProveedorNombreNull()
    {
        // Arrange: tenant SIN ruteo de proveedor (comportamiento 1-a-1 clásico)
        string tenantSlug;
        string secret;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var tenant = new Tenant
            {
                Name = "Tienda WooCommerce Clasica",
                Slug = "tienda-wc-clasica",
                TargetUrl = "https://shop-clasica.example.com/webhooks",
                WebhookSecret = "wc-test-secret-clasico",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                RuteoProveedorActivo = false,  // sin ruteo de proveedor
                ProveedorRuteoNombre = null,
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantSlug = tenant.Slug;
            secret = tenant.WebhookSecret;
        }

        var payload = """{"id":9876,"event":"order.created"}""";
        var body = Encoding.UTF8.GetBytes(payload);
        var signature = ComputarHmacBase64(secret, body);

        var client = BuildClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ingest/{tenantSlug}")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-WC-Webhook-Signature", signature);

        // Act
        var response = await client.SendAsync(request);

        // Assert: 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // El webhook encolado debe tener ProveedorNombre=null (flujo 1-a-1 intacto)
        var encolado = _factory.CapturingQueue.LastEnqueued;
        Assert.NotNull(encolado);
        Assert.Null(encolado!.ProveedorNombre);
    }

    // ─── Factory ──────────────────────────────────────────────────────────────

    public sealed class IngestWooFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } =
            Path.Combine(Path.GetTempPath(), $"gateway-ingest-woo-{Guid.NewGuid():N}.db");

        public CapturingIngestQueueService CapturingQueue { get; } = new();

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
                // Quitar hosted services (worker, tunnel, system log background, etc.)
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(d);
                // Reemplazar la cola por un capturing fake
                services.RemoveAll<RedisQueueService>();
                services.AddSingleton(CapturingQueue);
                services.AddSingleton<RedisQueueService>(sp => sp.GetRequiredService<CapturingIngestQueueService>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    public sealed class CapturingIngestQueueService : RedisQueueService
    {
        public QueuedWebhook? LastEnqueued { get; private set; }

        public CapturingIngestQueueService()
            : base(redis: null, new ConfigurationBuilder().Build(),
                   NullLogger<RedisQueueService>.Instance) { }

        public override Task EnqueueAsync(QueuedWebhook webhook)
        {
            LastEnqueued = webhook;
            return Task.CompletedTask;
        }
    }
}
