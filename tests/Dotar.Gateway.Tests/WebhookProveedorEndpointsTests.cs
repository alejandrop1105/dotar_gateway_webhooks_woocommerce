using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dotar.Gateway.Tests;

/// <summary>
/// Tests de integración del endpoint POST /webhook/{proveedor}.
/// Verifican: proveedor inexistente → 404, cuenta externa desconocida → 404,
/// firma inválida → 401, firma válida → 202 + webhook encolado,
/// y no-regresión del flujo 1-a-1 de IngestEndpoints.
/// </summary>
public class WebhookProveedorEndpointsTests : IClassFixture<WebhookProveedorFactory>, IAsyncLifetime
{
    private readonly WebhookProveedorFactory _factory;
    private Tenant _tenant = null!;

    public WebhookProveedorEndpointsTests(WebhookProveedorFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

        if (!await db.Tenants.AnyAsync())
        {
            _tenant = new Tenant
            {
                Name = "Proveedor Test Tenant",
                Slug = "prov-tenant",
                TargetUrl = "https://ejemplo.com/webhooks",
                WebhookSecret = "wc-secret-test",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(_tenant);
            await db.SaveChangesAsync();

            // Sembrar ProveedorWebhookConfig para el tenant con CuentaExternaId "123456789"
            var protector = dp.CreateProtector("ProveedorWebhookConfig.Credenciales.v1");
            var credenciales = JsonSerializer.Serialize(new
            {
                access_token = "TEST_ACCESS_TOKEN",
                signing_secret = "test-signing-secret-mp"
            });
            var credencialesCifradas = protector.Protect(credenciales);

            db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
            {
                TenantId = _tenant.Id,
                ProveedorNombre = "mercadopago",
                CuentaExternaId = "123456789",
                CredencialesCifradas = credencialesCifradas,
                BaseUrl = "https://api.mercadopago.com",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        else
        {
            _tenant = await db.Tenants.FirstAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient BuildClient() => _factory.CreateClient();

    /// <summary>
    /// Payload entrante de MP con user_id y data.id.
    /// </summary>
    private static byte[] BuildMpPayload(string userId, string dataId)
    {
        var json = $"{{\"user_id\":{userId},\"type\":\"payment\",\"data\":{{\"id\":\"{dataId}\"}}}}";
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Construye el header x-signature válido para un payload dado, usando el signing secret.
    /// manifest = "id:{dataId};ts:{ts};"  (sin request-id en estos tests)
    /// </summary>
    private static string BuildXSignature(string dataId, string ts, string signingSecret)
    {
        var manifest = $"id:{dataId};ts:{ts};";
        var keyBytes = Encoding.UTF8.GetBytes(signingSecret);
        var msgBytes = Encoding.UTF8.GetBytes(manifest);
        var hash = HMACSHA256.HashData(keyBytes, msgBytes);
        var v1 = Convert.ToHexString(hash).ToLowerInvariant();
        return $"ts={ts},v1={v1}";
    }

    // ─── Tests 5.1 – 5.5 ─────────────────────────────────────────────────────

    [Fact]
    public async Task WebhookProveedor_ProveedorInexistente_Retorna404()
    {
        // 5.1: ruta /webhook/stripe, proveedor sin keyed DI → 404
        var client = BuildClient();
        var body = BuildMpPayload("123456789", "9999");

        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/stripe");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task WebhookProveedor_CuentaExternaDesconocida_Retorna404()
    {
        // 5.2: user_id sin ProveedorWebhookConfig → 404 + log Ingest
        var client = BuildClient();
        // user_id 999999999 no tiene config asociada
        var body = BuildMpPayload("999999999", "1234");

        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/mercadopago");
        req.Headers.TryAddWithoutValidation("x-signature", "ts=1,v1=abc");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task WebhookProveedor_FirmaInvalida_Retorna401()
    {
        // 5.3: config resuelta pero x-signature inválida → 401 + log Auth
        var client = BuildClient();
        var body = BuildMpPayload("123456789", "5678");

        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/mercadopago");
        req.Headers.TryAddWithoutValidation("x-signature", "ts=1718000000000,v1=deadbeefdeadbeef");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WebhookProveedor_FirmaValida_Retorna202_YEncola()
    {
        // 5.4: payload MP válido → 202 + QueuedWebhook con TenantId + ProveedorNombre en Redis
        var fakeQueue = _factory.FakeQueue;
        fakeQueue.Limpiar();

        var dataId = "77777";
        var ts = "1718500000000";
        const string signingSecret = "test-signing-secret-mp";
        var body = BuildMpPayload("123456789", dataId);
        var xSig = BuildXSignature(dataId, ts, signingSecret);

        var client = BuildClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/mercadopago");
        req.Headers.TryAddWithoutValidation("x-signature", xSig);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Verificar que se encoló un QueuedWebhook con TenantId + ProveedorNombre
        var encolados = fakeQueue.ObtenerEncolados();
        Assert.Single(encolados);
        Assert.Equal(_tenant.Id, encolados[0].TenantId);
        Assert.Equal("mercadopago", encolados[0].ProveedorNombre);
    }

    /// <summary>
    /// CRITICAL 3: Config con IsActive=false → endpoint retorna 404 (cuenta desconocida).
    /// </summary>
    [Fact]
    public async Task WebhookProveedor_ConfigInactiva_Retorna404()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

        // Crear tenant y config inactiva
        var tenant = new Dotar.Gateway.Domain.Entities.Tenant
        {
            Name = "Inactivo Tenant",
            Slug = "inactivo-tenant",
            TargetUrl = "https://ejemplo.com/webhooks",
            WebhookSecret = "wc-secret",
            IsActive = true,
            SignatureScheme = Dotar.Gateway.Domain.Entities.SignatureScheme.WooCommerce,
            CreatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var protector = dp.CreateProtector("ProveedorWebhookConfig.Credenciales.v1");
        var creds = System.Text.Json.JsonSerializer.Serialize(new { access_token = "tok", signing_secret = "sec" });
        db.ProveedoresWebhookConfig.Add(new Dotar.Gateway.Domain.Entities.ProveedorWebhookConfig
        {
            TenantId = tenant.Id,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "INACTIVA-999",
            CredencialesCifradas = protector.Protect(creds),
            BaseUrl = "https://api.mercadopago.com",
            IsActive = false, // config desactivada
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var client = BuildClient();
        var body = BuildMpPayload("INACTIVA-999", "1");
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhook/mercadopago");
        req.Headers.TryAddWithoutValidation("x-signature", "ts=1,v1=abc");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task IngestEndpoint_NoRegresion_WooCommerce_Retorna202()
    {
        // 5.5: POST /ingest/{slug} con payload WooCommerce → comportamiento intacto
        var client = BuildClient();
        var body = """{"event":"order.created","order_id":42}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        // Calcular HMAC de WooCommerce (HMAC-SHA256 → Base64)
        var secretBytes = Encoding.UTF8.GetBytes("wc-secret-test");
        var hash = HMACSHA256.HashData(secretBytes, bodyBytes);
        var hmacBase64 = Convert.ToBase64String(hash);

        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/prov-tenant");
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Signature", hmacBase64);
        req.Headers.TryAddWithoutValidation("X-WC-Webhook-Topic", "order.created");
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        // El endpoint /ingest/{slug} debe seguir respondiendo 202 sin cambios
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }
}

// ─── Factory para tests de proveedor ──────────────────────────────────────────

public class WebhookProveedorFactory : GatewayWebApplicationFactory
{
    /// <summary>
    /// Cola fake que captura los webhooks encolados sin Redis real.
    /// </summary>
    public FakeRedisQueue FakeQueue { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seguridad:CallbackDominiosPermitidos:0"] = "*.cfargotunnel.com",
                ["Seguridad:CallbackDominiosPermitidos:1"] = "*.dotarsoluciones.com",
                ["Seguridad:CajaTtlMinutos"] = "30"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Reemplazar RedisQueueService con fake para capturar encolados
            var desc = services.FirstOrDefault(d => d.ServiceType == typeof(RedisQueueService));
            if (desc != null) services.Remove(desc);
            services.AddSingleton(FakeQueue);
            services.AddSingleton<RedisQueueService>(_ => FakeQueue);
        });
    }
}

/// <summary>
/// Fake de RedisQueueService: guarda en memoria los webhooks encolados para assertions.
/// </summary>
public class FakeRedisQueue : RedisQueueService
{
    private readonly List<Dotar.Gateway.Domain.Models.QueuedWebhook> _encolados = new();

    public FakeRedisQueue()
        : base(null, BuildConfig(), Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisQueueService>.Instance)
    { }

    public override Task EnqueueAsync(Dotar.Gateway.Domain.Models.QueuedWebhook webhook)
    {
        lock (_encolados) _encolados.Add(webhook);
        return Task.CompletedTask;
    }

    public List<Dotar.Gateway.Domain.Models.QueuedWebhook> ObtenerEncolados()
    {
        lock (_encolados) return [.. _encolados];
    }

    public void Limpiar()
    {
        lock (_encolados) _encolados.Clear();
    }

    private static IConfiguration BuildConfig()
        => new ConfigurationBuilder().Build();
}
