using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dotar.Gateway.Tests;

/// <summary>
/// Tests de integración del endpoint POST /registro-caja/{slug}.
/// Usa WebApplicationFactory con DB temporal aislada.
/// Verifica: HMAC válido → 200, inválido → 401, ausente → 401,
/// idempotencia, validaciones de campo, rate limit, tenant no encontrado.
/// </summary>
public class RegistroCajaEndpointsTests : IClassFixture<RegistroCajaFactory>, IAsyncLifetime
{
    private readonly RegistroCajaFactory _factory;

    public RegistroCajaEndpointsTests(RegistroCajaFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Sembrar tenant la primera vez (idempotente)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        if (!await db.Tenants.AnyAsync())
        {
            db.Tenants.Add(new Tenant
            {
                Name = "Test Caja Tenant",
                Slug = "caja-tenant",
                TargetUrl = "https://ejemplo.com/webhooks",
                WebhookSecret = "test-secret-base64",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Calcula HMAC-SHA256 hex lowercase del body con el secret del tenant.
    /// Esquema del endpoint: hex lowercase (no base64).
    /// </summary>
    private static string ComputarHmac(string secret, byte[] body)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(secretBytes, body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private HttpClient BuildClient() => _factory.CreateClient();

    private async Task<(Tenant tenant, string secret)> GetTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.FirstAsync();
        return (tenant, tenant.WebhookSecret);
    }

    private static StringContent BuildBody(string identificador, string callbackUrl)
    {
        var json = $$"""{"identificador":"{{identificador}}","callbackUrl":"{{callbackUrl}}"}""";
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistroCaja_HMAC_Valido_Retorna200()
    {
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA-01","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac(secret, bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Verificar que la caja quedó en DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var caja = await db.CajasRegistradas
            .FirstOrDefaultAsync(c => c.TenantId == tenant.Id && c.Identificador == "CAJA-01");
        Assert.NotNull(caja);
    }

    [Fact]
    public async Task RegistroCaja_HMAC_Invalido_Retorna401()
    {
        var (tenant, _) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA-02","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", "firma-invalida-hex");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_SinHMAC_Retorna401()
    {
        var (tenant, _) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA-03","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        // Sin header X-Caja-Signature
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_Idempotencia_ActualizaCallbackUrl()
    {
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        async Task Registrar(string identificador, string callbackUrl)
        {
            var body = $$"""{"identificador":"{{identificador}}","callbackUrl":"{{callbackUrl}}"}""";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var hmac = ComputarHmac(secret, bodyBytes);
            var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
            req.Headers.Add("X-Caja-Signature", hmac);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            r.EnsureSuccessStatusCode();
        }

        // Primer registro
        await Registrar("CAJA-IDEM", "https://tunel1.cfargotunnel.com/cb");

        // Re-registro con nueva URL
        await Registrar("CAJA-IDEM", "https://tunel2.cfargotunnel.com/cb");

        // Solo un registro en DB con la URL actualizada
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var cajas = await db.CajasRegistradas
            .Where(c => c.TenantId == tenant.Id && c.Identificador == "CAJA-IDEM")
            .ToListAsync();

        Assert.Single(cajas);
        Assert.Equal("https://tunel2.cfargotunnel.com/cb", cajas[0].CallbackUrl);
    }

    [Fact]
    public async Task RegistroCaja_IdentificadorConDobleColon_Retorna400()
    {
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA::01","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac(secret, bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_CallbackUrlHttp_Retorna400()
    {
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA-HTTP","callbackUrl":"http://tunel.cfargotunnel.com/cb"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac(secret, bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_CallbackUrlFueraDeAllowlist_Retorna400()
    {
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        var body = """{"identificador":"CAJA-EXT","callbackUrl":"https://externo.desconocido.com/cb"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac(secret, bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_TenantNoEncontrado_Retorna404()
    {
        var client = BuildClient();

        var body = """{"identificador":"CAJA-01","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac("cualquier-secret", bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, "/registro-caja/tenant-inexistente");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RegistroCaja_BodySuperaLimite_Retorna400()
    {
        // El límite del endpoint es 16 KB. Enviamos un payload de 17 KB.
        var (tenant, secret) = await GetTenantAsync();
        var client = BuildClient();

        // Construimos un body gigante (17 KB) — el HMAC viene calculado sobre él
        // pero el endpoint debe rechazar antes de validar el HMAC.
        var padding = new string('X', 17 * 1024);
        var body = $"{{\"identificador\":\"CAJA-BIG\",\"callbackUrl\":\"https://tunel.cfargotunnel.com/cb\",\"extra\":\"{padding}\"}}";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hmac = ComputarHmac(secret, bodyBytes);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{tenant.Slug}");
        req.Headers.Add("X-Caja-Signature", hmac);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

}

/// <summary>
/// Factory con config de allowlist para los tests de registro de caja.
/// El tenant se siembra en IAsyncLifetime.InitializeAsync del test class.
/// </summary>
public class RegistroCajaFactory : GatewayWebApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
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
    }
}

/// <summary>
/// Test de rate limiting aislado en su propia factory para no contaminar la ventana
/// del rate limiter del resto de los tests de integración.
/// </summary>
public class RegistroCaja_RateLimit_Test : IClassFixture<RegistroCajaRateLimitFactory>, IAsyncLifetime
{
    private readonly RegistroCajaRateLimitFactory _factory;
    private Tenant? _tenant;

    public RegistroCaja_RateLimit_Test(RegistroCajaRateLimitFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        if (!await db.Tenants.AnyAsync())
        {
            var t = new Tenant
            {
                Name = "RateLimit Tenant",
                Slug = "rl-tenant",
                TargetUrl = "https://ejemplo.com/webhooks",
                WebhookSecret = "rl-secret",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            _tenant = t;
        }
        else
        {
            _tenant = await db.Tenants.FirstAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RegistroCaja_RateLimit_Retorna429()
    {
        var secret = _tenant!.WebhookSecret;
        var slug = _tenant.Slug;

        var responses = new List<HttpResponseMessage>();

        for (var i = 0; i < 15; i++)
        {
            var client = _factory.CreateClient();
            var identificador = $"CAJA-RL-{i:D2}";
            var body = $$"""{"identificador":"{{identificador}}","callbackUrl":"https://tunel.cfargotunnel.com/cb"}""";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var hmac = ComputarHmacHex(secret, bodyBytes);

            var req = new HttpRequestMessage(HttpMethod.Post, $"/registro-caja/{slug}");
            req.Headers.Add("X-Caja-Signature", hmac);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            responses.Add(await client.SendAsync(req));
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    private static string ComputarHmacHex(string secret, byte[] body)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(secretBytes, body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Factory dedicada para el test de rate limiting.</summary>
public class RegistroCajaRateLimitFactory : RegistroCajaFactory { }
