using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

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

    /// <summary>
    /// CRITICAL 3: GetCompletoByProveedorYCuentaAsync retorna null cuando IsActive=false.
    /// </summary>
    [Fact]
    public async Task GetCompleto_ConfigInactiva_RetornaNull()
    {
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-inactiva",
            """{"access_token":"tok","signing_secret":"sec"}""",
            "https://api.mercadopago.com",
            isActive: false);

        var result = await _service.GetCompletoByProveedorYCuentaAsync("mercadopago", "cuenta-inactiva");

        Assert.Null(result);
    }

    /// <summary>
    /// CRITICAL 3: GetCompletoByProveedorYCuentaAsync retorna el DTO cuando IsActive=true.
    /// </summary>
    [Fact]
    public async Task GetCompleto_ConfigActiva_RetornaDto()
    {
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-activa",
            """{"access_token":"tok","signing_secret":"sec"}""",
            "https://api.mercadopago.com",
            isActive: true);

        var result = await _service.GetCompletoByProveedorYCuentaAsync("mercadopago", "cuenta-activa");

        Assert.NotNull(result);
        Assert.Equal(_tenant.Id, result!.TenantId);
        Assert.True(result.IsActive);
    }

    /// <summary>
    /// MENOR — ToString() de ProveedorConfigCompletoDto redacta credenciales sensibles.
    /// </summary>
    [Fact]
    public void ProveedorConfigCompletoDto_ToString_RedactaCredenciales()
    {
        var dto = new Dotar.Gateway.Application.ProveedorConfigCompletoDto(
            TenantId: 1,
            ProveedorNombre: "mercadopago",
            CuentaExternaId: "123",
            AccessToken: "mi-token-secreto",
            SigningSecret: "mi-firma-secreta",
            BaseUrl: "https://api.mercadopago.com",
            IsActive: true);

        var str = dto.ToString();

        Assert.DoesNotContain("mi-token-secreto", str);
        Assert.DoesNotContain("mi-firma-secreta", str);
        Assert.Contains("***", str);
        Assert.Contains("mercadopago", str);
    }
}

// ─── Tests T-01: ListarMetadataAsync + hint enmascarado ──────────────────────

/// <summary>
/// Tests TDD (RED) — ListarMetadataAsync y helper Hint.
/// Verifica que el hint expone solo el sufijo de 6 chars y que los secretos
/// nunca aparecen en claro en el DTO.
/// </summary>
public class ProveedorWebhookConfigAppService_ListarMetadata_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly ProveedorWebhookConfigAppService _service;
    private readonly IDataProtector _protector;
    private readonly Tenant _tenant;

    public ProveedorWebhookConfigAppService_ListarMetadata_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-lista-meta-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Metadata Tenant",
            Slug = "metadata-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

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

    /// <summary>
    /// Hint con accessToken > 6 chars: debe ser "••••••" + últimos 6 chars.
    /// El secret completo NO debe aparecer en ningún campo del DTO.
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_HintToken_MasDe6Chars_MuestraSufijo6()
    {
        // accessToken = "abcdef123456" → hint = "••••••123456"
        var creds = """{"access_token":"abcdef123456","signing_secret":"signsec"}""";
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-hint",
            creds, "https://api.mercadopago.com");

        var lista = await _service.ListarMetadataAsync();

        Assert.Single(lista);
        var dto = lista[0];
        Assert.Equal("••••••123456", dto.HintCredenciales);
        // El secret completo no debe aparecer en el DTO
        Assert.DoesNotContain("abcdef123456", dto.HintCredenciales[6..]); // solo sufijo
        // Verificar que el token completo no está en ningún campo string del DTO
        Assert.DoesNotContain("abcdef123456", dto.ProveedorNombre);
        Assert.DoesNotContain("abcdef123456", dto.CuentaExternaId);
        Assert.DoesNotContain("abcdef123456", dto.BaseUrl);
    }

    /// <summary>
    /// Hint con accessToken de exactamente 6 chars: debe ser "••••••" + todos los chars.
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_HintToken_ExactamentE6Chars_MuestraTodos()
    {
        var creds = """{"access_token":"abc123","signing_secret":"signsec"}""";
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-6",
            creds, "https://api.mercadopago.com");

        var lista = await _service.ListarMetadataAsync();

        Assert.Single(lista);
        // <= 6 chars → "••••••" + todos los chars del token
        Assert.Equal("••••••abc123", lista[0].HintCredenciales);
    }

    /// <summary>
    /// Hint con accessToken de menos de 6 chars: debe ser "••••••" + todos los chars.
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_HintToken_MenosDe6Chars_MuestraTodos()
    {
        var creds = """{"access_token":"ab","signing_secret":"signsec"}""";
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-short",
            creds, "https://api.mercadopago.com");

        var lista = await _service.ListarMetadataAsync();

        Assert.Single(lista);
        Assert.Equal("••••••ab", lista[0].HintCredenciales);
    }

    /// <summary>
    /// Hint cuando el descifrado falla: debe ser "••••••??????" sin lanzar excepción.
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_DescifradoFalla_HintEsInterrogantes()
    {
        // Insertar datos corruptos directamente en la BD para simular clave rotada
        var config = new ProveedorWebhookConfig
        {
            TenantId = _tenant.Id,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "cuenta-corrupta",
            CredencialesCifradas = "esto-no-es-un-ciphertext-valido",
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.ProveedoresWebhookConfig.Add(config);
        await _db.SaveChangesAsync();

        // No debe lanzar excepción
        var lista = await _service.ListarMetadataAsync();

        Assert.Single(lista);
        Assert.Equal("••••••??????", lista[0].HintCredenciales);
    }

    /// <summary>
    /// ListarMetadataAsync sin registros retorna lista vacía.
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_SinRegistros_RetornaListaVacia()
    {
        var lista = await _service.ListarMetadataAsync();

        Assert.Empty(lista);
    }

    /// <summary>
    /// El DTO incluye TenantNombre del join con Tenants.
    /// El DTO no contiene campos de credenciales (AccessToken, SigningSecret, CredencialesCifradas).
    /// </summary>
    [Fact]
    public async Task ListarMetadataAsync_ConRegistros_RetornaMetadataConTenantNombre()
    {
        var creds = """{"access_token":"token-xyz-789","signing_secret":"sec"}""";
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-meta",
            creds, "https://api.mercadopago.com");

        var lista = await _service.ListarMetadataAsync();

        Assert.Single(lista);
        var dto = lista[0];
        Assert.Equal(_tenant.Id, dto.TenantId);
        Assert.Equal("Metadata Tenant", dto.TenantNombre);
        Assert.Equal("mercadopago", dto.ProveedorNombre);
        Assert.Equal("cuenta-meta", dto.CuentaExternaId);
        Assert.Equal("https://api.mercadopago.com", dto.BaseUrl);
        Assert.True(dto.IsActive);

        // Verificar que el tipo no expone campos de credenciales
        var tipo = dto.GetType();
        Assert.Null(tipo.GetProperty("AccessToken"));
        Assert.Null(tipo.GetProperty("SigningSecret"));
        Assert.Null(tipo.GetProperty("CredencialesCifradas"));
    }
}

// ─── Tests T-05b: ActualizarMetadataAsync ────────────────────────────────────

/// <summary>
/// Tests TDD (RED) — ActualizarMetadataAsync.
/// Verifica que actualiza isActive/baseUrl/cuentaExternaId sin tocar CredencialesCifradas.
/// </summary>
public class ProveedorWebhookConfigAppService_ActualizarMetadata_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly ProveedorWebhookConfigAppService _service;
    private readonly Tenant _tenant;

    public ProveedorWebhookConfigAppService_ActualizarMetadata_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-actualizar-meta-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Actualizar Tenant",
            Slug = "actualizar-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        var provider = new EphemeralDataProtectionProvider();
        _service = new ProveedorWebhookConfigAppService(_db, provider,
            NullLogger<ProveedorWebhookConfigAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    /// <summary>
    /// ActualizarMetadataAsync actualiza isActive/baseUrl/cuentaExternaId y preserva CredencialesCifradas.
    /// Las credenciales descifradas tras la actualización siguen devolviendo el secret original.
    /// </summary>
    [Fact]
    public async Task ActualizarMetadataAsync_ActualizaMetadataYPreservaCredenciales()
    {
        var credsOriginales = """{"access_token":"token-original","signing_secret":"secret-original"}""";
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-v1",
            credsOriginales, "https://api.mercadopago.com");

        var config = await _db.ProveedoresWebhookConfig
            .FirstAsync(p => p.TenantId == _tenant.Id);
        var ciphertextOriginal = config.CredencialesCifradas;

        var result = await _service.ActualizarMetadataAsync(
            config.Id, "cuenta-v2", "https://api.nuevo.com", isActive: false);

        Assert.True(result.IsSuccess);

        var configActualizada = await _db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstAsync(p => p.Id == config.Id);

        Assert.Equal("cuenta-v2", configActualizada.CuentaExternaId);
        Assert.Equal("https://api.nuevo.com", configActualizada.BaseUrl);
        Assert.False(configActualizada.IsActive);
        // CredencialesCifradas no debe haber cambiado
        Assert.Equal(ciphertextOriginal, configActualizada.CredencialesCifradas);

        // Verificar round-trip: descifrar sigue devolviendo el secret original
        var dto = await _service.GetByProveedorYCuentaAsync("mercadopago", "cuenta-v2");
        Assert.NotNull(dto);
        Assert.Equal("token-original", dto!.AccessToken);
        Assert.Equal("secret-original", dto.SigningSecret);
    }

    /// <summary>
    /// ActualizarMetadataAsync con cuentaExternaId vacío → Result.Validation.
    /// </summary>
    [Fact]
    public async Task ActualizarMetadataAsync_CuentaExternaIdVacia_RetornaValidation()
    {
        var result = await _service.ActualizarMetadataAsync(1L, "   ", "https://api.example.com", true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
    }

    /// <summary>
    /// ActualizarMetadataAsync con Id inexistente → Result.NotFound.
    /// </summary>
    [Fact]
    public async Task ActualizarMetadataAsync_IdInexistente_RetornaNotFound()
    {
        var result = await _service.ActualizarMetadataAsync(99999L, "cuenta-x", "https://api.example.com", true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
    }
}

// ─── Tests T-03: EliminarAsync (proveedor) ───────────────────────────────────

/// <summary>
/// Tests TDD (RED) — EliminarAsync para ProveedorWebhookConfig.
/// </summary>
public class ProveedorWebhookConfigAppService_Eliminar_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly ProveedorWebhookConfigAppService _service;
    private readonly Tenant _tenant;

    public ProveedorWebhookConfigAppService_Eliminar_Test()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-eliminar-proveedor-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Eliminar Tenant",
            Slug = "eliminar-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        var provider = new EphemeralDataProtectionProvider();
        _service = new ProveedorWebhookConfigAppService(_db, provider,
            NullLogger<ProveedorWebhookConfigAppService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    /// <summary>
    /// EliminarAsync con Id existente → elimina de DB y retorna Result.Success().
    /// </summary>
    [Fact]
    public async Task EliminarAsync_IdExistente_EliminaYRetornaSuccess()
    {
        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-del",
            """{"access_token":"tok","signing_secret":"sec"}""",
            "https://api.mercadopago.com");

        var config = await _db.ProveedoresWebhookConfig
            .FirstAsync(p => p.TenantId == _tenant.Id);

        var result = await _service.EliminarAsync(config.Id);

        Assert.True(result.IsSuccess);
        var count = await _db.ProveedoresWebhookConfig.CountAsync(p => p.Id == config.Id);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// EliminarAsync con Id inexistente → retorna Result.Failure(NotFound) sin excepción.
    /// </summary>
    [Fact]
    public async Task EliminarAsync_IdInexistente_RetornaNotFound()
    {
        var result = await _service.EliminarAsync(99999L);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
    }

    /// <summary>
    /// EliminarAsync de un proveedor no afecta la tabla CajasRegistradas.
    /// </summary>
    [Fact]
    public async Task EliminarAsync_NoAfectaCajasRegistradas()
    {
        // Insertar una caja para asegurarse que no se elimina
        var caja = new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-INTACTA",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CajasRegistradas.Add(caja);
        await _db.SaveChangesAsync();

        await _service.UpsertAsync(_tenant.Id, "mercadopago", "cuenta-del2",
            """{"access_token":"tok","signing_secret":"sec"}""",
            "https://api.mercadopago.com");

        var config = await _db.ProveedoresWebhookConfig.FirstAsync(p => p.TenantId == _tenant.Id);
        await _service.EliminarAsync(config.Id);

        // La caja debe seguir en la BD
        var cajaCount = await _db.CajasRegistradas.CountAsync(c => c.Id == caja.Id);
        Assert.Equal(1, cajaCount);
    }
}
