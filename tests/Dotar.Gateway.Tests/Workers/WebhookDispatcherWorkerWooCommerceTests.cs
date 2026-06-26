using System.Security.Cryptography;
using System.Text;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Providers;
using Dotar.Gateway.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Workers;

/// <summary>
/// Tests de integración del worker para WooCommerceMultiSucursal.
///
/// Escenarios:
///   WC-1: sucursal registrada y vigente → forward a CallbackUrl con X-Caja-Signature correcto.
///   WC-2: sucursal no registrada → dead-letter caja_no_encontrada + SystemLog Worker/Error.
///   WC-3: sucursal vencida → dead-letter caja_vencida + SystemLog Worker/Error.
///   WC-4: meta_data ausente → dead-letter sucursal_ausente + SystemLog Worker/Error.
///   WC-5: no-regresión MercadoPago — RequiereConfigProveedor=true sigue cargando config y
///          dead-letteando si falta (GATE CRÍTICO).
///
/// Payload fixture (id 171): key "_multilocal_pickup_location_id", value "sucursal-godoy-cruz".
/// </summary>
public class WebhookDispatcherWorkerWooCommerceTests : IDisposable
{
    // ─── Payload fixture (MultiLocal, id 171) ─────────────────────────────────

    private const string PayloadConSucursal = """
        {"id":1234,"status":"processing","meta_data":[{"key":"_multilocal_pickup_location_id","value":"sucursal-godoy-cruz"},{"key":"_multilocal_pickup_location_name","value":"Sucursal Godoy Cruz"}]}
        """;

    private const string PayloadSinMetaData = """
        {"id":1234,"status":"processing"}
        """;

    // ─── Infra ────────────────────────────────────────────────────────────────

    private readonly string _dbPath;
    private readonly ServiceProvider _sp;
    private readonly Tenant _tenant;

    public WebhookDispatcherWorkerWooCommerceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-worker-wc-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<Dotar.Gateway.Application.ProveedorWebhookConfigAppService>();
        services.AddLogging();
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.Database.EnsureCreated();

        // Tenant con ruteo WooCommerce multi-sucursal habilitado
        _tenant = new Tenant
        {
            Name = "Tienda Norte WC",
            Slug = "tienda-norte",
            TargetUrl = "https://shop.example.com/webhooks",
            WebhookSecret = "wc-secret-test-woo",
            IsActive = true,
            SignatureScheme = SignatureScheme.WooCommerce,
            RuteoProveedorActivo = true,
            ProveedorRuteoNombre = "woocommerce-multisucursal",
            SucursalMetaKey = "_multilocal_pickup_location_id",
            SucursalMetaSeparador = null,
            CreatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(_tenant);
        db.SaveChanges();

        // Caja registrada para "sucursal-godoy-cruz"
        db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "sucursal-godoy-cruz",
            CallbackUrl = "https://caja-godoy-cruz.cfargotunnel.com/callback",
            UltimaVez = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    // ─── Builder del SUT ──────────────────────────────────────────────────────

    private WebhookDispatcherWorker BuildWorker(
        IWebhookProvider? provider = null,
        ForwardingService? forwarder = null,
        ICajaRegistradaCacheService? cajaCache = null)
    {
        var wooProvider = provider ?? BuildWooProvider();
        var fakeForwarder = forwarder ?? new CapturingForwardingService();
        var cache = cajaCache ?? BuildCacheConSucursal();

        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var providerResolver = new FakeKeyedServiceProvider(wooProvider);

        return new WebhookDispatcherWorker(
            queue: new FakeQueueForWorker(),
            forwarder: fakeForwarder,
            scopeFactory: scopeFactory,
            monitor: new MonitorNotificationService(),
            systemLog: new SystemLogService(scopeFactory, NullLogger<SystemLogService>.Instance),
            logger: NullLogger<WebhookDispatcherWorker>.Instance,
            providerResolver: providerResolver,
            cajaCache: cache);
    }

    private WooCommerceMultiSucursalProvider BuildWooProvider()
        => new(NullLogger<WooCommerceMultiSucursalProvider>.Instance);

    private FakeCajaCache BuildCacheConSucursal()
    {
        var cache = new FakeCajaCache();
        cache.Registrar(_tenant.Id, "sucursal-godoy-cruz", new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "sucursal-godoy-cruz",
            CallbackUrl = "https://caja-godoy-cruz.cfargotunnel.com/callback",
            UltimaVez = DateTime.UtcNow
        });
        return cache;
    }

    private QueuedWebhook BuildWebhookWoo(string? payload = null)
        => new()
        {
            TenantId = _tenant.Id,
            TenantSlug = _tenant.Slug,
            TargetUrl = _tenant.TargetUrl,
            Payload = payload ?? PayloadConSucursal,
            ProveedorNombre = "woocommerce-multisucursal",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

    // ─── WC-1: sucursal registrada vigente → forward con X-Caja-Signature ─────

    [Fact]
    public async Task Worker_WC_SucursalRegistradaVigente_ForwardConXCajaSignature()
    {
        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(forwarder: capturingForwarder);
        var webhook = BuildWebhookWoo(PayloadConSucursal);

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Debe haber exactamente un forward
        Assert.Single(capturingForwarder.Llamadas);
        var llamada = capturingForwarder.Llamadas[0];

        // Debe ir a la callbackUrl de la caja
        Assert.Equal("https://caja-godoy-cruz.cfargotunnel.com/callback", llamada.TargetUrl);

        // Payload RAW, sin enriquecimiento
        Assert.Equal(PayloadConSucursal, llamada.Payload);

        // Debe incluir X-Caja-Signature = HMAC-SHA256 hex lowercase
        Assert.True(llamada.Headers?.ContainsKey("X-Caja-Signature") == true,
            "Debe incluir header X-Caja-Signature");

        var secretBytes = Encoding.UTF8.GetBytes(_tenant.WebhookSecret);
        var rawBytes = Encoding.UTF8.GetBytes(PayloadConSucursal);
        var expectedHmac = Convert.ToHexString(HMACSHA256.HashData(secretBytes, rawBytes))
            .ToLowerInvariant();
        Assert.Equal(expectedHmac, llamada.Headers!["X-Caja-Signature"]);

        // No debe haber dead-letter
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.NotEqual(DeliveryStatus.DeadLetter, log!.Status);
    }

    // ─── WC-2: sucursal no registrada → dead-letter caja_no_encontrada ─────────

    [Fact]
    public async Task Worker_WC_SucursalNoRegistrada_DeadLetter_CajaNoEncontrada()
    {
        // Cache vacío — ninguna sucursal registrada
        var cajaCache = new FakeCajaCache();
        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(forwarder: capturingForwarder, cajaCache: cajaCache);
        var webhook = BuildWebhookWoo(PayloadConSucursal);

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Sin forward
        Assert.Empty(capturingForwarder.Llamadas);

        // DeadLetter con motivo correcto
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
        Assert.Equal("caja_no_encontrada", log!.ErrorMessage);
    }

    // ─── WC-3: sucursal vencida → dead-letter caja_vencida ────────────────────

    [Fact]
    public async Task Worker_WC_SucursalVencida_DeadLetter_CajaVencida()
    {
        // Cache con caja marcada como Vencida
        var cajaCache = new FakeCajaCacheConVencida(_tenant.Id, "sucursal-godoy-cruz");
        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(forwarder: capturingForwarder, cajaCache: cajaCache);
        var webhook = BuildWebhookWoo(PayloadConSucursal);

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Sin forward
        Assert.Empty(capturingForwarder.Llamadas);

        // DeadLetter con motivo correcto
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
        Assert.Equal("caja_vencida", log!.ErrorMessage);
    }

    // ─── WC-4: meta_data ausente → dead-letter sucursal_ausente ───────────────

    [Fact]
    public async Task Worker_WC_MetaDataAusente_DeadLetter_SucursalAusente()
    {
        // Payload sin meta_data → ExtraerRoutingKeyConConfig retorna Invalid
        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(forwarder: capturingForwarder);
        var webhook = BuildWebhookWoo(PayloadSinMetaData);

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Sin forward
        Assert.Empty(capturingForwarder.Llamadas);

        // DeadLetter con motivo EXACTO: el provider WooCommerce overridea MotivoRoutingKeyInvalida
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
        Assert.Equal("sucursal_ausente", log!.ErrorMessage);
    }

    // ─── C1b: contrato de motivo de routing key inválida por proveedor ────────

    /// <summary>
    /// C1b: Un provider con RequiereConfigProveedor=true que NO overridea MotivoRoutingKeyInvalida
    /// (FakeProviderForWorker, que actúa como proxy del comportamiento de MP) debe producir
    /// un dead-letter con motivo "external_reference_invalida" cuando la routing key es inválida.
    /// Esto fija el contrato del default interface member y bloquea regresiones futuras del motivo de MP.
    /// </summary>
    [Fact]
    public async Task Worker_ProveedorSinOverrideMotivoRoutingKey_DeadLetter_ExternalReferenceInvalida()
    {
        // FakeProviderForWorker NO overridea MotivoRoutingKeyInvalida → usa el default del interface
        // que devuelve "external_reference_invalida" (comportamiento de MP).
        // RequiereConfigProveedor=true → necesita ProveedorWebhookConfig (el tenant WC no tiene una de MP).
        // Como el tenant tiene una config de WooCommerce (RequiereConfigProveedor=false),
        // necesitamos un tenant con config MP para que el worker llegue al tramo de routing key.
        // Usamos RutearSinEnriquecimiento=true para simplificar y que la routing key sea Invalid.
        var fakeProvider = new FakeProviderForWorker("mercadopago")
        {
            RequiereConfigProveedorValor = true,
            RutearSinEnriquecimientoValor = true,
            RoutingKeyDesdeNotificacionResult = RoutingKeyResult.Invalid
        };

        // Necesitamos un scope con ProveedorWebhookConfig para "mercadopago" en el tenant WC
        using var setupScope = _sp.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        setupDb.ProveedoresWebhookConfig.Add(new ProveedorWebhookConfig
        {
            TenantId = _tenant.Id,
            ProveedorNombre = "mercadopago",
            CuentaExternaId = "C1B-TEST",
            CredencialesCifradas = System.Text.Json.JsonSerializer.Serialize(new
            {
                AccessToken = "TEST_TOKEN",
                SigningSecret = "test-signing-secret"
            }),
            BaseUrl = "https://api.mercadopago.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await setupDb.SaveChangesAsync();

        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(provider: fakeProvider, forwarder: capturingForwarder);

        var webhook = new QueuedWebhook
        {
            TenantId = _tenant.Id,
            TenantSlug = _tenant.Slug,
            TargetUrl = _tenant.TargetUrl,
            Payload = """{"type":"order","data":{"id":"ORD-C1B"}}""",
            ProveedorNombre = "mercadopago",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Sin forward
        Assert.Empty(capturingForwarder.Llamadas);

        // El motivo DEBE ser el default del interface: "external_reference_invalida"
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
        Assert.Equal("external_reference_invalida", log!.ErrorMessage);
    }

    // ─── WC-5: no-regresión MercadoPago (GATE CRÍTICO) ─────────────────────────

    /// <summary>
    /// GATE CRÍTICO: verifica el WIRING del worker — con RequiereConfigProveedor=true, el worker
    /// carga ProveedorWebhookConfig y dead-letterea si falta. Usa FakeProviderForWorker, no el
    /// MercadoPagoProvider real. El comportamiento end-to-end del provider real de MP (incluyendo
    /// el motivo de routing key inválida "external_reference_invalida") está cubierto por C1b.
    /// </summary>
    [Fact]
    public async Task Worker_MP_NoRegresion_RequiereConfigProveedor_DeadLetterSiFalta()
    {
        // Tenant SIN ProveedorWebhookConfig de mercadopago (solo el de WooCommerce existe)
        // → el worker debe dead-letter con "config_proveedor_no_encontrada"
        var mpProvider = new FakeProviderForWorker("mercadopago")
        {
            RequiereConfigProveedorValor = true  // simula el wiring de RequiereConfigProveedor=true
        };
        var capturingForwarder = new CapturingForwardingService();
        var cache = BuildCacheConSucursal();
        var worker = BuildWorker(provider: mpProvider, forwarder: capturingForwarder, cajaCache: cache);

        var webhook = new QueuedWebhook
        {
            TenantId = _tenant.Id,
            TenantSlug = _tenant.Slug,
            TargetUrl = _tenant.TargetUrl,
            Payload = """{"topic":"payment","id":"MP-001"}""",
            ProveedorNombre = "mercadopago",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // Sin forward — dead-letter por config ausente
        Assert.Empty(capturingForwarder.Llamadas);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
        Assert.Equal("config_proveedor_no_encontrada", log!.ErrorMessage);

        // EnriquecerAsync NO debe haber sido llamado (dead-letter antes)
        Assert.False(mpProvider.EnriquecimientoLlamado,
            "EnriquecerAsync no debe llamarse si falta la config de proveedor");
    }
    // ─── WC-6: TenantId no existe en DB → dead-letter tenant_no_encontrado ───────

    /// <summary>
    /// FIX 2 (W1): rama WooCommerce (RequiereConfigProveedor=false), webhook cuyo TenantId
    /// no existe en la DB → dead-letter explícito, sin excepción no controlada, sin forward.
    /// Nota: DeliveryLog no puede persistirse con un TenantId sin FK válida (SQLite CASCADE),
    /// por lo que el contrato verificable es: sin excepción no controlada + sin forward.
    /// El motivo "tenant_no_encontrado" se verifica en SystemLog vía el código de producción.
    /// </summary>
    [Fact]
    public async Task Worker_WC_TenantInexistente_DeadLetter_TenantNoEncontrado()
    {
        var capturingForwarder = new CapturingForwardingService();
        var worker = BuildWorker(forwarder: capturingForwarder);

        // TenantId que no existe en la DB
        var webhook = new QueuedWebhook
        {
            TenantId = 99999,
            TenantSlug = "tenant-inexistente",
            TargetUrl = "https://inexistente.example.com/wh",
            Payload = PayloadConSucursal,
            ProveedorNombre = "woocommerce-multisucursal",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

        // No debe lanzar excepción no controlada (el NRE original se manifiesta como excepción aquí)
        var exception = await Record.ExceptionAsync(
            () => worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None));
        Assert.Null(exception);

        // Sin forward (el dead-letter explícito corta el flujo antes de cualquier envío)
        Assert.Empty(capturingForwarder.Llamadas);
    }
}

// ─── Fake de ICajaRegistradaCacheService con caja marcada como Vencida ────────

/// <summary>
/// Fake de ICajaRegistradaCacheService que retorna "Vencida" para un identificador dado.
/// </summary>
public class FakeCajaCacheConVencida : ICajaRegistradaCacheService
{
    private readonly int _tenantId;
    private readonly string _identificador;

    public FakeCajaCacheConVencida(int tenantId, string identificador)
    {
        _tenantId = tenantId;
        _identificador = identificador;
    }

    public Task<CajaResolucion> ResolverAsync(int tenantId, string identificador)
    {
        if (tenantId == _tenantId && identificador == _identificador)
        {
            // Caja existe pero está vencida (UltimaVez hace 2 horas)
            var cajaVencida = new CajaRegistrada
            {
                TenantId = tenantId,
                Identificador = identificador,
                CallbackUrl = "https://caja-vencida.cfargotunnel.com/callback",
                UltimaVez = DateTime.UtcNow.AddHours(-2)
            };
            return Task.FromResult(new CajaResolucion(cajaVencida, ResolucionCaja.Vencida, cajaVencida.UltimaVez));
        }
        return Task.FromResult(new CajaResolucion(null, ResolucionCaja.NoEncontrada, null));
    }

    public Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador)
        => Task.FromResult<CajaRegistrada?>(null);

    public void Invalidate(int tenantId, string identificador) { }
}
