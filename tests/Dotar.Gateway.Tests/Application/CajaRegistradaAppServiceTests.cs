using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

// ─── Fakes de infraestructura ─────────────────────────────────────────────────

/// <summary>Fake de ICajaRegistradaCacheService para tests unitarios del AppService.</summary>
internal sealed class FakeCajaCache : ICajaRegistradaCacheService
{
    public List<(int TenantId, string Identificador)> InvalidatedKeys { get; } = [];

    public Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador)
        => Task.FromResult<CajaRegistrada?>(null);

    public void Invalidate(int tenantId, string identificador)
        => InvalidatedKeys.Add((tenantId, identificador));
}

// ─── Tests de validación anti-SSRF ───────────────────────────────────────────

/// <summary>
/// Tests unitarios de CajaRegistradaAppService.RegistrarAsync — validación anti-SSRF.
/// Verifica que URLs no-HTTPS o fuera de allowlist sean rechazadas con Result.Validation.
/// </summary>
public class CajaRegistradaAppService_AntiSSRF_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly FakeCajaCache _cache;
    private readonly CajaRegistradaAppService _service;
    private readonly Tenant _tenant;

    private static IConfiguration BuildConfig(string[] allowList)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < allowList.Length; i++)
            dict[$"Seguridad:CallbackDominiosPermitidos:{i}"] = allowList[i];
        dict["Seguridad:CajaTtlMinutos"] = "30";
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    public CajaRegistradaAppService_AntiSSRF_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ssrf-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Test Tenant",
            Slug = "test-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        _cache = new FakeCajaCache();
        var config = BuildConfig(["*.cfargotunnel.com", "*.dotarsoluciones.com"]);
        _service = new CajaRegistradaAppService(_db, _cache, config,
            NullLogger<CajaRegistradaAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlHttp_DevuelveValidation()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "http://tunel.cfargotunnel.com/cb");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlFueraDeAllowlist_DevuelveValidation()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "https://externo-desconocido.com/cb");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlEnAllowlist_DevuelveSuccess()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "https://tunel.cfargotunnel.com/cb");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlSubdominioDotarsoluciones_DevuelveSuccess()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-02", "https://webhook.dotarsoluciones.com/cb");

        Assert.True(result.IsSuccess);
    }
}

// ─── Tests de validación de longitud (anti-abuso) ────────────────────────────

/// <summary>
/// Tests unitarios de CajaRegistradaAppService.RegistrarAsync — límites de longitud.
/// Verifica que identificador > 100 chars y callbackUrl > 2000 chars sean rechazados.
/// </summary>
public class CajaRegistradaAppService_Longitud_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly CajaRegistradaAppService _service;
    private readonly Tenant _tenant;

    public CajaRegistradaAppService_Longitud_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-len-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Len Tenant",
            Slug = "len-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        var cache = new FakeCajaCache();
        var dict = new Dictionary<string, string?>
        {
            ["Seguridad:CallbackDominiosPermitidos:0"] = "*.cfargotunnel.com",
            ["Seguridad:CajaTtlMinutos"] = "30"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        _service = new CajaRegistradaAppService(_db, cache, config,
            NullLogger<CajaRegistradaAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task RegistrarAsync_Identificador101Chars_DevuelveValidation()
    {
        var identificadorLargo = new string('X', 101);

        var result = await _service.RegistrarAsync(
            _tenant.Id, identificadorLargo, "https://tunel.cfargotunnel.com/cb");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task RegistrarAsync_Identificador100Chars_DevuelveSuccess()
    {
        var identificador100 = new string('X', 100);

        var result = await _service.RegistrarAsync(
            _tenant.Id, identificador100, "https://tunel.cfargotunnel.com/cb");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrl2001Chars_DevuelveValidation()
    {
        // Base url: "https://tunel.cfargotunnel.com/" = 31 chars; relleno con path hasta 2001
        var base_ = "https://tunel.cfargotunnel.com/";
        var callbackUrlLarga = base_ + new string('a', 2001 - base_.Length);
        Assert.Equal(2001, callbackUrlLarga.Length);

        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-LEN", callbackUrlLarga);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrl2000Chars_DevuelveSuccess()
    {
        var base_ = "https://tunel.cfargotunnel.com/";
        var callbackUrl2000 = base_ + new string('a', 2000 - base_.Length);
        Assert.Equal(2000, callbackUrl2000.Length);

        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-LEN2", callbackUrl2000);

        Assert.True(result.IsSuccess);
    }
}

// ─── Tests de validación de puerto (anti-SSRF: puerto no estándar) ────────────

/// <summary>
/// Tests unitarios de CajaRegistradaAppService.RegistrarAsync — rechazo de puerto no-default.
/// Verifica que HTTPS con puerto explícito != 443 sea rechazado.
/// </summary>
public class CajaRegistradaAppService_Puerto_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly CajaRegistradaAppService _service;
    private readonly Tenant _tenant;

    public CajaRegistradaAppService_Puerto_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-puerto-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Puerto Tenant",
            Slug = "puerto-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        var cache = new FakeCajaCache();
        var dict = new Dictionary<string, string?>
        {
            ["Seguridad:CallbackDominiosPermitidos:0"] = "*.cfargotunnel.com",
            ["Seguridad:CajaTtlMinutos"] = "30"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        _service = new CajaRegistradaAppService(_db, cache, config,
            NullLogger<CajaRegistradaAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlConPuerto8080_DevuelveValidation()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-P1", "https://tunel.cfargotunnel.com:8080/cb");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlConPuerto443Explicito_DevuelveSuccess()
    {
        // Uri.IsDefaultPort es false cuando el puerto es explícito pero igual al default.
        // La validación debe aceptar puerto 443 explícito.
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-P2", "https://tunel.cfargotunnel.com:443/cb");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RegistrarAsync_CallbackUrlSinPuerto_DevuelveSuccess()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-P3", "https://tunel.cfargotunnel.com/cb");

        Assert.True(result.IsSuccess);
    }
}

// ─── Tests de upsert idempotente ──────────────────────────────────────────────

/// <summary>
/// Tests unitarios de CajaRegistradaAppService.RegistrarAsync — idempotencia.
/// Verifica upsert: nuevo → inserta; re-registro → actualiza sin duplicar.
/// </summary>
public class CajaRegistradaAppService_Upsert_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly FakeCajaCache _cache;
    private readonly CajaRegistradaAppService _service;
    private readonly Tenant _tenant;

    public CajaRegistradaAppService_Upsert_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-upsert-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Upsert Tenant",
            Slug = "upsert-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        _cache = new FakeCajaCache();
        var dict = new Dictionary<string, string?>
        {
            ["Seguridad:CallbackDominiosPermitidos:0"] = "*.cfargotunnel.com",
            ["Seguridad:CallbackDominiosPermitidos:1"] = "*.dotarsoluciones.com",
            ["Seguridad:CajaTtlMinutos"] = "30"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        _service = new CajaRegistradaAppService(_db, _cache, config,
            NullLogger<CajaRegistradaAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task RegistrarAsync_NuevaCaja_PersisteCaja()
    {
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "https://tunel.cfargotunnel.com/cb");

        Assert.True(result.IsSuccess);
        var count = await _db.CajasRegistradas
            .CountAsync(c => c.TenantId == _tenant.Id && c.Identificador == "CAJA-01");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegistrarAsync_ReRegistro_ActualizaCallbackUrlSinDuplicar()
    {
        // Primer registro
        await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "https://tunel1.cfargotunnel.com/cb");

        // Segundo registro con misma caja, distinta URL
        var result = await _service.RegistrarAsync(
            _tenant.Id, "CAJA-01", "https://tunel2.cfargotunnel.com/cb");

        Assert.True(result.IsSuccess);

        // Solo una entrada
        var cajas = await _db.CajasRegistradas
            .Where(c => c.TenantId == _tenant.Id && c.Identificador == "CAJA-01")
            .ToListAsync();
        Assert.Single(cajas);
        Assert.Equal("https://tunel2.cfargotunnel.com/cb", cajas[0].CallbackUrl);
    }

    [Fact]
    public async Task RegistrarAsync_Exitoso_InvalidaCache()
    {
        await _service.RegistrarAsync(
            _tenant.Id, "CAJA-03", "https://tunel.cfargotunnel.com/cb");

        Assert.Contains((_tenant.Id, "CAJA-03"), _cache.InvalidatedKeys);
    }
}
