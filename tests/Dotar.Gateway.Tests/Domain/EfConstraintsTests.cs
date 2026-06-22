using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Tests.Domain;

/// <summary>
/// Tests de restricciones EF para las entidades CajaRegistrada y ProveedorWebhookConfig.
/// Usan SQLite in-memory con la misma configuración que GatewayDbContext.
/// </summary>
public class EfConstraintsTests : IDisposable
{
    private readonly GatewayDbContext _db;

    public EfConstraintsTests()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new GatewayDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        // Seed del tenant requerido para las FK
        _db.Tenants.Add(new Tenant
        {
            Id = 1,
            Name = "Tenant Prueba",
            Slug = "tenant-prueba",
            WebhookSecret = "secreto",
            TargetUrl = "https://destino.test/hook",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    // ─── 1.1 CajaRegistrada: índice único (TenantId, Identificador) ───

    [Fact]
    public async Task CajaRegistrada_Indice_TenantId_Identificador_Unico()
    {
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = 1,
            Identificador = "CAJA-01",
            CallbackUrl = "https://caja.test/webhook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Duplicado con mismo TenantId + Identificador debe fallar
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = 1,
            Identificador = "CAJA-01",
            CallbackUrl = "https://otro.test/webhook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task CajaRegistrada_Permite_MismoIdentificador_DiferenteTenant()
    {
        // Tenants diferentes pueden tener el mismo Identificador — no es violación
        _db.Tenants.Add(new Tenant
        {
            Id = 2,
            Name = "Tenant B",
            Slug = "tenant-b",
            WebhookSecret = "secreto2",
            TargetUrl = "https://b.test/hook",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = 1,
            Identificador = "CAJA-01",
            CallbackUrl = "https://a.test/webhook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = 2,
            Identificador = "CAJA-01",
            CallbackUrl = "https://b.test/webhook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // No debe lanzar — son tenants distintos
        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.CajasRegistradas.CountAsync());
    }

    [Fact]
    public async Task CajaRegistrada_DeleteCascade_Desde_Tenant()
    {
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = 1,
            Identificador = "CAJA-CASCADE",
            CallbackUrl = "https://cascade.test/webhook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var tenant = await _db.Tenants.FindAsync(1);
        _db.Tenants.Remove(tenant!);
        await _db.SaveChangesAsync();

        Assert.Equal(0, await _db.CajasRegistradas.CountAsync());
    }

    // ─── 1.2 ProveedorWebhookConfig: índices únicos ───

    [Fact]
    public async Task ProveedorWebhookConfig_Indice_TenantId_ProveedorNombre_Unico()
    {
        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "123456",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Duplicado (TenantId, ProveedorNombre) debe fallar
        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "999999",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task ProveedorWebhookConfig_Indice_ProveedorNombre_CuentaExternaId_UnicoGlobal()
    {
        _db.Tenants.Add(new Tenant
        {
            Id = 2,
            Name = "Tenant B",
            Slug = "tenant-b",
            WebhookSecret = "secreto2",
            TargetUrl = "https://b.test/hook",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "CUENTA-DUPLICADA",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Diferente tenant pero misma CuentaExternaId + ProveedorNombre → violación global
        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 2,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "CUENTA-DUPLICADA",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task ProveedorWebhookConfig_Permite_MismaCuentaExterna_DistintoProveedor()
    {
        // Misma CuentaExternaId pero distinto ProveedorNombre → no es violación
        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "CUENTA-X",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = 1,
            ProveedorNombre = "stripe",
            CuentaExternaId = "CUENTA-X",
            CredencialesCifradas = "{}",
            BaseUrl = "https://api.stripe.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.ProveedoresWebhookConfig.CountAsync());
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }
}
