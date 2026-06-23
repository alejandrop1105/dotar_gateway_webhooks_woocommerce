using System.Net;
using System.Text;
using System.Text.Json;
using Dotar.Gateway.Application;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Providers;
using Dotar.Gateway.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Workers;

/// <summary>
/// Tests que cierran el gap del bug de descifrado de credenciales en el worker.
///
/// PROBLEMA HISTÓRICO: los tests del worker usaban FakeProviderForWorker, lo que ocultaba
/// el mismatch entre el ciphertext almacenado en ProveedorWebhookConfig.CredencialesCifradas
/// y el JSON en claro que MercadoPagoProvider espera leer. El bug pasó desapercibido porque
/// el fake provider nunca intentaba parsear las credenciales.
///
/// Estos tests usan el MercadoPagoProvider REAL con un HttpMessageHandler fake para capturar
/// el header Authorization enviado. Esto expone el bug directamente: con ciphertext en
/// CredencialesCifradas, ExtraerAccessToken devuelve null → Bearer vacío → 401 en producción.
/// </summary>
public class DescifradoCredencialesWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;
    private readonly Tenant _tenant;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public DescifradoCredencialesWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-descifrado-{Guid.NewGuid():N}.db");

        // EphemeralDataProtectionProvider: cifra en memoria, no necesita claves en disco.
        // Idéntico al que usa ProveedorWebhookConfigAppService en producción (misma purpose string).
        _dataProtectionProvider = new EphemeralDataProtectionProvider();

        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));

        // Registrar ProveedorWebhookConfigAppService con el proveedor de protección efímero.
        // Esto es lo que necesita el worker para descifrar credenciales en el fix.
        services.AddSingleton<IDataProtectionProvider>(_dataProtectionProvider);
        services.AddScoped<ProveedorWebhookConfigAppService>();
        services.AddLogging();

        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Tenant Descifrado Test",
            Slug = "tenant-descifrado",
            TargetUrl = "https://ejemplo.com/webhooks",
            WebhookSecret = "wc-descifrado-secret",
            IsActive = true,
            SignatureScheme = SignatureScheme.WooCommerce,
            CreatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(_tenant);
        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    // ─── Helper: sembrar config cifrada con el AppService (camino real) ───────

    private async Task<ProveedorWebhookConfig> SembrarConfigCifradaAsync(
        string accessToken = "TOKEN-ABC-123",
        string signingSecret = "signing-secret-test",
        bool isActive = true)
    {
        using var scope = _sp.CreateScope();
        var appService = scope.ServiceProvider.GetRequiredService<ProveedorWebhookConfigAppService>();

        // UpsertAsync cifra las credenciales con IDataProtector antes de persistir.
        // Esto replica el camino real: la UI guarda → el worker lee ciphertext.
        var credencialesJson = JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            signing_secret = signingSecret
        });

        var result = await appService.UpsertAsync(
            _tenant.Id, "mercadopago", "cuenta-test-123",
            credencialesJson, "https://api.mercadopago.com", isActive);

        Assert.True(result.IsSuccess, "La siembra de config cifrada falló.");

        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        return await db.ProveedoresWebhookConfig
            .AsNoTracking()
            .FirstAsync(p => p.TenantId == _tenant.Id && p.ProveedorNombre == "mercadopago");
    }

    // ─── RED: Bug actual — ciphertext pasado al provider → Bearer vacío ───────

    /// <summary>
    /// RED (falla con el bug actual):
    /// Cuando el worker pasa la entidad RAW (ciphertext en CredencialesCifradas) al
    /// MercadoPagoProvider real, ExtraerAccessToken falla al parsear el ciphertext como JSON
    /// y devuelve null. El header Authorization queda como "Bearer " (vacío) en lugar de
    /// "Bearer TOKEN-ABC-123".
    ///
    /// CIERRA EL GAP DEL MOCK: a diferencia de FakeProviderForWorker (que ignora las
    /// credenciales), el MercadoPagoProvider real expone el mismatch entre ciphertext y JSON.
    ///
    /// Este test FALLA con el código actual (Bearer vacío) y PASA con el fix (Bearer correcto).
    /// </summary>
    [Fact]
    public async Task Worker_ConCredencialesCifradas_MercadoPagoProviderReal_UsaBearerCorrecto()
    {
        // ARRANGE: sembrar config con credenciales CIFRADAS (camino real vía AppService)
        var configEntidad = await SembrarConfigCifradaAsync(accessToken: "TOKEN-ABC-123");

        // Verificar que CredencialesCifradas es realmente ciphertext (no JSON en claro)
        Assert.False(configEntidad.CredencialesCifradas.StartsWith("{"),
            "CredencialesCifradas debe ser ciphertext, no JSON en claro");
        Assert.DoesNotContain("TOKEN-ABC-123", configEntidad.CredencialesCifradas);

        // Handler capturador: intercepta la llamada HTTP real que hace el provider
        HttpRequestMessage? requestCapturado = null;
        var handlerFake = new CapturingAuthorizationHandler(
            HttpStatusCode.OK,
            """{"id":12345,"external_reference":"CAJA-01::00001","status":"approved"}""",
            r => requestCapturado = r);

        var mpProvider = new MercadoPagoProvider(
            new HttpClient(handlerFake),
            NullLogger<MercadoPagoProvider>.Instance);

        // Cajas y forwarder
        var cajaCache = new FakeCajaCache();
        cajaCache.Registrar(_tenant.Id, "CAJA-01", new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-01",
            CallbackUrl = "https://caja1.cfargotunnel.com/callback",
            UltimaVez = DateTime.UtcNow
        });

        var capturingForwarder = new CapturingForwardingService();

        var worker = ConstruirWorker(mpProvider, capturingForwarder, cajaCache);

        var webhook = new QueuedWebhook
        {
            TenantId = _tenant.Id,
            TenantSlug = _tenant.Slug,
            TargetUrl = _tenant.TargetUrl,
            Payload = """{"topic":"payment","data":{"id":"12345"}}""",
            ProveedorNombre = "mercadopago",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

        // ACT: procesar el webhook con el provider real
        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // ASSERT: el provider real debe haber sido llamado con el token correcto
        Assert.NotNull(requestCapturado);
        var authHeader = requestCapturado!.Headers.Authorization;
        Assert.NotNull(authHeader);

        // Con el BUG actual: Bearer es vacío → Authorization = "Bearer "
        // Con el FIX:        Bearer tiene el token → Authorization = "Bearer TOKEN-ABC-123"
        // Si falla acá, el bug es que el worker pasó ciphertext al provider en lugar de JSON en claro.
        Assert.Equal("Bearer TOKEN-ABC-123", authHeader!.ToString());
    }

    /// <summary>
    /// Complementario: verifica que la lógica de descifrado/reconstrucción de ProveedorWebhookConfig
    /// produce un objeto que MercadoPagoProvider puede leer correctamente (AccessToken y SigningSecret).
    ///
    /// Ejercita directamente GetByTenantYProveedorAsync → reconstrucción PascalCase →
    /// ExtraerAccessToken/ExtraerSigningSecret en el provider.
    ///
    /// CIERRA EL GAP: confirma que el formato JSON en claro PascalCase que produce el fix
    /// es el mismo que el provider espera leer.
    /// </summary>
    [Fact]
    public async Task ConfigReconstruidaDesdeDescifrado_EsLegiblePorMercadoPagoProvider()
    {
        // ARRANGE
        const string expectedToken = "MI-ACCESS-TOKEN-XYZ";
        const string expectedSecret = "MI-SIGNING-SECRET-ABC";

        await SembrarConfigCifradaAsync(accessToken: expectedToken, signingSecret: expectedSecret);

        // Simular el camino del fix: obtener DTO descifrado y reconstruir la entidad
        ProveedorWebhookConfig configReconstruida;
        using (var scope = _sp.CreateScope())
        {
            var appService = scope.ServiceProvider.GetRequiredService<ProveedorWebhookConfigAppService>();
            var dto = await appService.GetByTenantYProveedorAsync(_tenant.Id, "mercadopago");

            Assert.NotNull(dto);

            // Reconstruir la entidad con CredencialesCifradas = JSON en claro PascalCase
            // (exactamente como lo hace el endpoint y como debe hacerlo el fix del worker)
            configReconstruida = new ProveedorWebhookConfig
            {
                TenantId = _tenant.Id,
                ProveedorNombre = "mercadopago",
                CuentaExternaId = "cuenta-test-123",
                CredencialesCifradas = JsonSerializer.Serialize(new
                {
                    SigningSecret = dto!.SigningSecret,
                    AccessToken = dto.AccessToken
                }),
                BaseUrl = dto.BaseUrl,
                IsActive = dto.IsActive
            };
        }

        // ASSERT: el provider real puede leer las credenciales de la entidad reconstruida
        HttpRequestMessage? requestCapturado = null;
        var handlerFake = new CapturingAuthorizationHandler(
            HttpStatusCode.OK,
            """{"id":1,"external_reference":"CAJA-01::001","status":"approved"}""",
            r => requestCapturado = r);

        var mpProvider = new MercadoPagoProvider(
            new HttpClient(handlerFake),
            NullLogger<MercadoPagoProvider>.Instance);

        var resultado = await mpProvider.EnriquecerAsync("1", configReconstruida, CancellationToken.None);

        Assert.True(resultado.Exitoso);
        Assert.NotNull(requestCapturado);
        Assert.Equal($"Bearer {expectedToken}", requestCapturado!.Headers.Authorization!.ToString());
    }

    /// <summary>
    /// Config inactiva (IsActive=false): el worker debe dead-letter en lugar de intentar enriquecer.
    /// Verifica que el filtro IsActive se mantiene coherente en el fix.
    /// </summary>
    [Fact]
    public async Task Worker_ConfigInactiva_Descifrado_DeadLetter()
    {
        // Sembrar config inactiva
        await SembrarConfigCifradaAsync(accessToken: "TOKEN-INACTIVO", isActive: false);

        HttpRequestMessage? requestCapturado = null;
        var handlerFake = new CapturingAuthorizationHandler(
            HttpStatusCode.OK, "{}", r => requestCapturado = r);

        var mpProvider = new MercadoPagoProvider(
            new HttpClient(handlerFake),
            NullLogger<MercadoPagoProvider>.Instance);

        var capturingForwarder = new CapturingForwardingService();
        var worker = ConstruirWorker(mpProvider, capturingForwarder, new FakeCajaCache());

        var webhook = new QueuedWebhook
        {
            TenantId = _tenant.Id,
            TenantSlug = _tenant.Slug,
            TargetUrl = _tenant.TargetUrl,
            Payload = """{"topic":"payment","data":{"id":"99"}}""",
            ProveedorNombre = "mercadopago",
            EventId = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow
        };

        await worker.ProcesarWebhookParaTestAsync(webhook, CancellationToken.None);

        // No debe haber llamada HTTP al proveedor ni forwarding
        Assert.Null(requestCapturado);
        Assert.Empty(capturingForwarder.Llamadas);

        // Debe haber un DeadLetter
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var log = await db.DeliveryLogs.FirstOrDefaultAsync(l => l.WebhookEventId == webhook.EventId);
        Assert.NotNull(log);
        Assert.Equal(DeliveryStatus.DeadLetter, log!.Status);
    }

    // ─── Helpers de construcción ──────────────────────────────────────────────

    private WebhookDispatcherWorker ConstruirWorker(
        IWebhookProvider provider,
        ForwardingService forwarder,
        FakeCajaCache cajaCache)
    {
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var providerResolver = new FakeKeyedServiceProvider(provider);

        return new WebhookDispatcherWorker(
            queue: new FakeQueueForWorker(),
            forwarder: forwarder,
            scopeFactory: scopeFactory,
            monitor: new MonitorNotificationService(),
            systemLog: new SystemLogService(scopeFactory, NullLogger<SystemLogService>.Instance),
            logger: NullLogger<WebhookDispatcherWorker>.Instance,
            providerResolver: providerResolver,
            cajaCache: cajaCache);
    }
}

/// <summary>
/// HttpMessageHandler que captura la request y devuelve una respuesta configurable.
/// Permite verificar que el header Authorization contiene el Bearer token correcto.
/// </summary>
public sealed class CapturingAuthorizationHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;
    private readonly Action<HttpRequestMessage> _capture;

    public CapturingAuthorizationHandler(
        HttpStatusCode statusCode,
        string body,
        Action<HttpRequestMessage> capture)
    {
        _statusCode = statusCode;
        _body = body;
        _capture = capture;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _capture(request);
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
