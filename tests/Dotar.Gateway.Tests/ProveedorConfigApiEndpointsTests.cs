using System.Net;
using System.Net.Http.Json;
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
/// Tests de integración del admin API POST|GET /api/proveedores/config.
/// Verifican: autenticación por API Key, upsert (nuevo→201, existente→200 sin duplicado),
/// y que el listado no exponga credenciales sensibles.
/// </summary>
public class ProveedorConfigApiEndpointsTests : IClassFixture<ProveedorConfigApiFactory>, IAsyncLifetime
{
    private readonly ProveedorConfigApiFactory _factory;

    public ProveedorConfigApiEndpointsTests(ProveedorConfigApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Sembrar un tenant base para los tests
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        if (!await db.Tenants.AnyAsync(t => t.Slug == "config-api-tenant"))
        {
            db.Tenants.Add(new Tenant
            {
                Name = "Config API Tenant",
                Slug = "config-api-tenant",
                TargetUrl = "https://ejemplo.com/webhooks",
                WebhookSecret = "config-api-secret",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string ApiKey => _factory.Services.GetRequiredService<ApiKeyService>().GetCurrent()!;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, ApiKey);
        return client;
    }

    private async Task<int> GetTenantIdAsync(string slug = "config-api-tenant")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == slug);
        return tenant.Id;
    }

    private static object BuildUpsertBody(
        string tenantSlug,
        string proveedorNombre,
        string cuentaExternaId,
        string accessToken = "token-test",
        string signingSecret = "secret-test",
        string baseUrl = "https://api.mercadopago.com",
        bool isActive = true)
    {
        return new
        {
            tenant = tenantSlug,
            proveedorNombre,
            cuentaExternaId,
            accessToken,
            signingSecret,
            baseUrl,
            isActive
        };
    }

    // ─── 6.1: Sin autenticación → 401 ────────────────────────────────────────

    [Fact]
    public async Task ProveedorConfig_SinAutenticacion_Retorna401()
    {
        var client = _factory.CreateClient(); // sin API key

        var body = BuildUpsertBody("config-api-tenant", "mercadopago", "111111111");

        // POST sin autenticación
        var respPost = await client.PostAsJsonAsync("/api/proveedores/config", body);
        Assert.Equal(HttpStatusCode.Unauthorized, respPost.StatusCode);

        // GET sin autenticación
        var respGet = await client.GetAsync("/api/proveedores/config");
        Assert.Equal(HttpStatusCode.Unauthorized, respGet.StatusCode);
    }

    // ─── 6.2: Upsert nuevo → 201 + credenciales cifradas en DB ──────────────

    [Fact]
    public async Task ProveedorConfig_UpsertNuevo_Retorna201()
    {
        var client = AuthedClient();

        // Usar un cuentaExternaId único para no pisar otros tests
        var cuentaId = $"nuevo-{Guid.NewGuid():N}".Substring(0, 20);

        var body = BuildUpsertBody(
            tenantSlug: "config-api-tenant",
            proveedorNombre: "mercadopago",
            cuentaExternaId: cuentaId,
            accessToken: "access-token-nuevo",
            signingSecret: "signing-secret-nuevo");

        var resp = await client.PostAsJsonAsync("/api/proveedores/config", body);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Verificar persistencia con credenciales cifradas (no en texto plano)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenantId = await GetTenantIdAsync();

        var config = await db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProveedorNombre == "mercadopago" && p.CuentaExternaId == cuentaId);

        Assert.NotNull(config);
        Assert.False(string.IsNullOrWhiteSpace(config!.CredencialesCifradas));

        // Las credenciales cifradas NO deben contener el access token en texto plano
        Assert.DoesNotContain("access-token-nuevo", config.CredencialesCifradas);
        Assert.DoesNotContain("signing-secret-nuevo", config.CredencialesCifradas);
    }

    // ─── 6.3: Upsert existente → 200 + sin duplicado en DB ──────────────────

    [Fact]
    public async Task ProveedorConfig_UpsertExistente_Retorna200_SinDuplicado()
    {
        var client = AuthedClient();

        // Usar un proveedor nombre único para este test para aislar el índice único
        var proveedorNombre = $"mp-{Guid.NewGuid():N}".Substring(0, 15);
        var cuentaId = $"cuenta-{Guid.NewGuid():N}".Substring(0, 15);

        // Necesitamos crear un tenant dedicado con slug único para este proveedorNombre
        // (el índice único es por (TenantId, ProveedorNombre))
        var tenantSlug = $"dup-{Guid.NewGuid():N}".Substring(0, 20);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.Tenants.Add(new Tenant
            {
                Name = "Dup Test Tenant",
                Slug = tenantSlug,
                TargetUrl = "https://ejemplo.com/webhooks",
                WebhookSecret = "dup-secret",
                IsActive = true,
                SignatureScheme = SignatureScheme.WooCommerce,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Primera inserción → 201
        var body1 = BuildUpsertBody(tenantSlug, proveedorNombre, cuentaId,
            accessToken: "token-v1", signingSecret: "secret-v1");
        var resp1 = await client.PostAsJsonAsync("/api/proveedores/config", body1);
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);

        // Segunda inserción para mismo (TenantId, ProveedorNombre) → 200
        var body2 = BuildUpsertBody(tenantSlug, proveedorNombre, $"cuenta-nueva-{Guid.NewGuid():N}".Substring(0, 15),
            accessToken: "token-v2", signingSecret: "secret-v2");
        var resp2 = await client.PostAsJsonAsync("/api/proveedores/config", body2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        // Verificar un único registro en DB para este (TenantId, ProveedorNombre)
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenantId = await db2.Tenants
            .Where(t => t.Slug == tenantSlug)
            .Select(t => t.Id)
            .FirstAsync();

        var count = await db2.ProveedoresWebhookConfig
            .CountAsync(p => p.TenantId == tenantId && p.ProveedorNombre == proveedorNombre);

        Assert.Equal(1, count);
    }

    // ─── 6.4: Listar NO expone credenciales ──────────────────────────────────

    [Fact]
    public async Task ProveedorConfig_Listar_NoExponeCredenciales()
    {
        var client = AuthedClient();
        var tenantId = await GetTenantIdAsync();

        // Asegurar que hay al menos una config en DB para este tenant
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

            if (!await db.ProveedoresWebhookConfig.AnyAsync(p => p.TenantId == tenantId))
            {
                var protector = dp.CreateProtector("ProveedorWebhookConfig.Credenciales.v1");
                var creds = JsonSerializer.Serialize(new
                {
                    access_token = "SUPER_SECRET_TOKEN",
                    signing_secret = "SUPER_SECRET_SIGNING"
                });
                db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
                {
                    TenantId = tenantId,
                    ProveedorNombre = "mercadopago",
                    CuentaExternaId = "listar-test-cuenta",
                    CredencialesCifradas = protector.Protect(creds),
                    BaseUrl = "https://api.mercadopago.com",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        var resp = await client.GetAsync("/api/proveedores/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var rawJson = await resp.Content.ReadAsStringAsync();

        // La respuesta NO debe contener el access token ni el secret en texto plano
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER_SECRET_SIGNING", rawJson, StringComparison.OrdinalIgnoreCase);

        // Tampoco debe contener los campos sensibles (ninguno en cualquier casing)
        Assert.DoesNotContain("access_token", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signingSecret", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signing_secret", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credencialesCifradas", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretProveedor", rawJson, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Factory para tests del admin API de configuración de proveedor.
/// Hereda de GatewayWebApplicationFactory (usa DB temporal aislada y sin workers).
/// </summary>
public class ProveedorConfigApiFactory : GatewayWebApplicationFactory
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
                ["Seguridad:CajaTtlMinutos"] = "30",
                ["MercadoPago:BaseUrl"] = "https://api.mercadopago.com"
            });
        });
    }
}
