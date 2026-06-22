using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de ProveedorWebhookConfigAppService — cifrado round-trip.
/// Usa IDataProtector efímero (EphemeralDataProtectionProvider) para no requerir claves en disco.
/// </summary>
public class ProveedorWebhookConfigAppService_Cifrado_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly ProveedorWebhookConfigAppService _service;
    private readonly Tenant _tenant;
    private readonly IDataProtector _protector;

    public ProveedorWebhookConfigAppService_Cifrado_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-cifrado-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Config Tenant",
            Slug = "config-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        // Proveedor efímero: no persiste claves; perfecto para tests unitarios
        var provider = new EphemeralDataProtectionProvider();
        _protector = provider.CreateProtector("ProveedorWebhookConfig.Credenciales.v1");
        _service = new ProveedorWebhookConfigAppService(_db, provider,
            NullLogger<ProveedorWebhookConfigAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_CifraCredenciales_NoPersistePlanoEnDB()
    {
        var credenciales = """{"access_token":"token-secreto","signing_secret":"s3cr3t"}""";

        await _service.UpsertAsync(_tenant.Id, "mercadopago", "user-123",
            credenciales, "https://api.mercadopago.com");

        var entidad = await _db.ProveedoresWebhookConfig
            .FirstAsync(p => p.TenantId == _tenant.Id);

        // El ciphertext no debe contener el token en claro
        Assert.DoesNotContain("token-secreto", entidad.CredencialesCifradas);
        Assert.DoesNotContain("s3cr3t", entidad.CredencialesCifradas);
    }

    [Fact]
    public async Task UpsertAsync_RoundTrip_DescifraCorrectamente()
    {
        var credenciales = """{"access_token":"mi-token","signing_secret":"mi-signing"}""";

        var result = await _service.UpsertAsync(_tenant.Id, "mercadopago", "user-456",
            credenciales, "https://api.mercadopago.com");

        Assert.True(result.IsSuccess);

        // Obtener y descifrar desde el servicio (no desde la entidad directamente)
        var dto = await _service.GetByProveedorYCuentaAsync("mercadopago", "user-456");
        Assert.NotNull(dto);
        Assert.Equal("mi-token", dto!.AccessToken);
        Assert.Equal("mi-signing", dto.SigningSecret);
    }

    [Fact]
    public async Task UpsertAsync_CredencialesNoExpuestaEnDTO()
    {
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "user-789",
            """{"access_token":"secreto","signing_secret":"firmado"}""",
            "https://api.mercadopago.com");

        // El lookup retorna un DTO — verificamos que tiene los valores necesarios
        // pero que la entidad en DB está cifrada (ya verificado en test anterior)
        var dto = await _service.GetByTenantYProveedorAsync(_tenant.Id, "mercadopago");
        Assert.NotNull(dto);
        // El DTO debe exponer los valores descifrados (para uso interno del gateway)
        Assert.Equal("secreto", dto!.AccessToken);
    }

    [Fact]
    public async Task UpsertAsync_IdempotentePorProveedorYCuenta_NoDuplica()
    {
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-idp",
            """{"access_token":"token1","signing_secret":"s1"}""",
            "https://api.mercadopago.com");

        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-idp",
            """{"access_token":"token2","signing_secret":"s2"}""",
            "https://api.mercadopago.com");

        var count = await _db.ProveedoresWebhookConfig
            .CountAsync(p => p.TenantId == _tenant.Id && p.ProveedorNombre == "mercadopago");
        Assert.Equal(1, count);
    }
}
