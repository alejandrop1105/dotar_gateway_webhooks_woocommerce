using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Polly;
using Polly.CircuitBreaker;

namespace Dotar.Gateway.Workers;

/// <summary>
/// Worker que consume webhooks de Redis y los reenvía al destino final.
/// Implementa un scheduler de reintentos basado en pasos configurables por política.
///
/// Bifurca por QueuedWebhook.ProveedorNombre:
///   - null → flujo 1-a-1 intacto (ForwardAsync a TargetUrl del tenant)
///   - no null → flujo de proveedor: enriquecer → extraer routing key → buscar caja → reenviar RAW
///
/// CB keyed por callbackUrl (cap 500, TTL deslizante 30 min) para evitar fuga de memoria.
/// </summary>
public class WebhookDispatcherWorker : BackgroundService
{
    private readonly RedisQueueService _queue;
    private readonly ForwardingService _forwarder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorNotificationService _monitor;
    private readonly SystemLogService _systemLog;
    private readonly ILogger<WebhookDispatcherWorker> _logger;
    private readonly IKeyedServiceProvider _providerResolver;
    private readonly ICajaRegistradaCacheService _cajaCache;

    // CB keyed por callbackUrl (string). Cap 500 entradas con TTL deslizante de 30 min.
    // Se usa MemoryCache concreto para poder llamar Clear() sin cast en runtime.
    private const int MaxCbEntries = 500;
    private static readonly TimeSpan CbTtl = TimeSpan.FromMinutes(30);
    private readonly MemoryCache _cbCache;

    // El flujo 1-a-1 original usaba ConcurrentDictionary<int, ...> keyed por TenantId/PolicyId.
    // Ahora usamos IMemoryCache keyed por callbackUrl para unificar ambos flujos.

    public WebhookDispatcherWorker(
        RedisQueueService queue,
        ForwardingService forwarder,
        IServiceScopeFactory scopeFactory,
        MonitorNotificationService monitor,
        SystemLogService systemLog,
        ILogger<WebhookDispatcherWorker> logger,
        IKeyedServiceProvider providerResolver,
        ICajaRegistradaCacheService cajaCache)
    {
        _queue = queue;
        _forwarder = forwarder;
        _scopeFactory = scopeFactory;
        _monitor = monitor;
        _systemLog = systemLog;
        _logger = logger;
        _providerResolver = providerResolver;
        _cajaCache = cajaCache;

        _cbCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = MaxCbEntries
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDispatcherWorker iniciado.");

        var consumeTask = ConsumeQueueLoopAsync(stoppingToken);
        var retryTask = RetrySchedulerLoopAsync(stoppingToken);

        await Task.WhenAll(consumeTask, retryTask);

        _logger.LogInformation("WebhookDispatcherWorker detenido.");
    }

    // ═══════════════════════════════════════════════════════
    // LOOP 1: Consumir cola Redis
    // ═══════════════════════════════════════════════════════

    private async Task ConsumeQueueLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var webhook = await _queue.DequeueAsync(timeoutSeconds: 5);
                if (webhook is null)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }
                await ProcessNewWebhookAsync(webhook, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("Circuit breaker abierto, esperando 10s...");
                await Task.Delay(10000, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en consume loop");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ProcessNewWebhookAsync(QueuedWebhook webhook, CancellationToken ct)
    {
        if (webhook.ProveedorNombre is null)
        {
            // ── Flujo 1-a-1 (WooCommerce / ingest clásico): comportamiento original intacto ──
            await ProcesarFlujo1a1Async(webhook, ct);
        }
        else
        {
            // ── Flujo de proveedor: enriquecer → rutear → forward RAW con firma ──
            await ProcesarFlujoProveedorAsync(webhook, ct);
        }
    }

    /// <summary>
    /// Punto de entrada testeable sin necesitar Redis. Public para acceso desde tests.
    /// </summary>
    public Task ProcesarWebhookParaTestAsync(QueuedWebhook webhook, CancellationToken ct)
        => ProcessNewWebhookAsync(webhook, ct);

    // ─── Flujo 1-a-1 (original, sin cambios observables) ─────────────────────

    private async Task ProcesarFlujo1a1Async(QueuedWebhook webhook, CancellationToken ct)
    {
        _logger.LogInformation("Procesando webhook 1-a-1 para '{Slug}' → {Url}",
            webhook.TenantSlug, webhook.TargetUrl);

        var eventId = webhook.EventId == Guid.Empty ? Guid.NewGuid() : webhook.EventId;

        _systemLog.Info(SystemLogCategory.Worker,
            $"Despachando webhook para '{webhook.TenantSlug}' → {webhook.TargetUrl}",
            tenantSlug: webhook.TenantSlug,
            eventId: eventId,
            url: webhook.TargetUrl,
            details: $"headers={webhook.ForwardedHeaders.Count}; payloadBytes={webhook.Payload?.Length ?? 0}");

        var result = await ForwardWithCircuitBreakerAsync(webhook.TargetUrl, webhook, ct);

        if (result.IsSuccess)
        {
            _systemLog.Info(SystemLogCategory.Forward,
                $"Reenvío exitoso a {webhook.TargetUrl} → HTTP {result.StatusCode} en {result.DurationMs}ms",
                tenantSlug: webhook.TenantSlug,
                eventId: eventId,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: webhook.TargetUrl);
        }
        else
        {
            _systemLog.Error(SystemLogCategory.Forward,
                $"Reenvío falló: {result.ErrorMessage}",
                tenantSlug: webhook.TenantSlug,
                eventId: eventId,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: webhook.TargetUrl,
                responseBody: result.ResponseBody,
                details: $"headersForwarded={webhook.ForwardedHeaders.Count}",
                ex: result.Exception);
        }

        await SaveDeliveryLogAsync(webhook, result, eventId, attemptNumber: 1, currentStep: 0,
            result.IsSuccess ? DeliveryStatus.Success : DeliveryStatus.Scheduled);
    }

    // ─── Flujo de proveedor (nuevo) ───────────────────────────────────────────

    private async Task ProcesarFlujoProveedorAsync(QueuedWebhook webhook, CancellationToken ct)
    {
        var eventId = webhook.EventId == Guid.Empty ? Guid.NewGuid() : webhook.EventId;
        var proveedorNombre = webhook.ProveedorNombre!;

        _logger.LogInformation(
            "Procesando webhook de proveedor '{Proveedor}' para tenant {TenantId}",
            proveedorNombre, webhook.TenantId);

        // 1. Resolver IWebhookProvider por keyed DI
        var provider = _providerResolver.GetKeyedService<IWebhookProvider>(proveedorNombre);
        if (provider is null)
        {
            _logger.LogError(
                "No se encontró IWebhookProvider para '{Proveedor}'. Webhook {EventId} a dead-letter.",
                proveedorNombre, eventId);
            _systemLog.Error(SystemLogCategory.Forward,
                $"Proveedor '{proveedorNombre}' no registrado en el worker. Dead-letter.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}");
            await SaveDeadLetterAsync(webhook, eventId, "proveedor_no_registrado");
            return;
        }

        // 2. Obtener ProveedorWebhookConfig del tenant para el enriquecimiento.
        //    Se incluye el Tenant para obtener WebhookSecret en el mismo scope (evita segundo scope).
        //    Además se descifran las credenciales vía ProveedorWebhookConfigAppService para no pasar
        //    el ciphertext al provider (que espera JSON en claro con AccessToken y SigningSecret).
        ProveedorWebhookConfig? configEntidad;
        ProveedorWebhookConfig? configParaProvider;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            configEntidad = await db.ProveedoresWebhookConfig
                .Include(p => p.Tenant)
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.TenantId == webhook.TenantId &&
                    p.ProveedorNombre == proveedorNombre, ct);

            if (configEntidad is not null)
            {
                // Descifrar credenciales con el AppService y reconstruir la entidad con JSON en claro.
                // Idéntico al patrón del endpoint WebhookProveedorEndpoints que ya funciona bien.
                var configAppService = scope.ServiceProvider
                    .GetRequiredService<Application.ProveedorWebhookConfigAppService>();
                var credencialesDescifradas = await configAppService
                    .GetByTenantYProveedorAsync(webhook.TenantId, proveedorNombre);

                // Filtrar inactivas coherentemente con GetCompletoByProveedorYCuentaAsync
                if (credencialesDescifradas is null || !credencialesDescifradas.IsActive)
                {
                    configParaProvider = null;
                }
                else
                {
                    configParaProvider = new ProveedorWebhookConfig
                    {
                        TenantId = configEntidad.TenantId,
                        ProveedorNombre = configEntidad.ProveedorNombre,
                        CuentaExternaId = configEntidad.CuentaExternaId,
                        CredencialesCifradas = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            SigningSecret = credencialesDescifradas.SigningSecret,
                            AccessToken = credencialesDescifradas.AccessToken
                        }),
                        BaseUrl = configEntidad.BaseUrl,
                        IsActive = configEntidad.IsActive,
                        Tenant = configEntidad.Tenant
                    };
                }
            }
            else
            {
                configParaProvider = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error al obtener ProveedorWebhookConfig para tenant {TenantId} y proveedor '{Proveedor}'",
                webhook.TenantId, proveedorNombre);
            await SaveDeadLetterAsync(webhook, eventId, "error_config_proveedor");
            return;
        }

        if (configEntidad is null || configParaProvider is null)
        {
            var motivo = configEntidad is null
                ? "config_proveedor_no_encontrada"
                : "config_proveedor_inactiva";
            _logger.LogWarning(
                "Sin config activa de proveedor '{Proveedor}' para tenant {TenantId}. Dead-letter ({Motivo}).",
                proveedorNombre, webhook.TenantId, motivo);
            _systemLog.Warn(SystemLogCategory.Forward,
                $"Sin config activa de proveedor '{proveedorNombre}' para tenant {webhook.TenantId}. Dead-letter.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; motivo={motivo}");
            await SaveDeadLetterAsync(webhook, eventId, motivo);
            return;
        }

        // 3. Bifurcación por tipo de notificación:
        //    - order → rutear directo desde data.external_reference del RAW, sin llamar a la API de MP.
        //    - payment (o sin type) → flujo de enriquecimiento existente, intacto.
        RoutingKeyResult routingKeyResult;

        if (provider.RutearSinEnriquecimiento(webhook.Payload))
        {
            // Rama order: sin enriquecimiento
            _systemLog.Info(SystemLogCategory.Worker,
                $"Notificación MP tipo 'order' detectada — modo sin_enriquecimiento.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; modo=sin_enriquecimiento");

            routingKeyResult = provider.ExtraerRoutingKeyDesdeNotificacion(webhook.Payload);
        }
        else
        {
            // Rama payment / default: flujo de enriquecimiento existente (intacto)
            _systemLog.Info(SystemLogCategory.Worker,
                $"Notificación MP tipo 'payment' detectada — modo con_enriquecimiento.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; modo=con_enriquecimiento");

            // 3a. Extraer idEvento del payload (ej. data.id en MP).
            //     Si no se puede extraer, dead-letter inmediato sin llamar EnriquecerAsync con id vacío.
            var idEvento = ExtraerIdEvento(webhook.Payload);
            if (string.IsNullOrEmpty(idEvento))
            {
                _logger.LogWarning(
                    "No se pudo extraer idEvento del payload para proveedor '{Proveedor}' tenant {TenantId}. Dead-letter.",
                    proveedorNombre, webhook.TenantId);
                _systemLog.Warn(SystemLogCategory.Forward,
                    $"No se pudo extraer idEvento del payload para proveedor '{proveedorNombre}'. Dead-letter.",
                    eventId: eventId,
                    details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; motivo=id_evento_no_extraible");
                await SaveDeadLetterAsync(webhook, eventId, "id_evento_no_extraible");
                return;
            }

            // 3b. Enriquecer contra la API del proveedor usando la config con credenciales descifradas
            var enrichmentResult = await provider.EnriquecerAsync(idEvento, configParaProvider, ct);

            if (!enrichmentResult.Exitoso)
            {
                _logger.LogWarning(
                    "Enriquecimiento fallido para proveedor '{Proveedor}' tenant {TenantId}: {Error}. Dead-letter.",
                    proveedorNombre, webhook.TenantId, enrichmentResult.ErrorMessage);
                _systemLog.Warn(SystemLogCategory.Forward,
                    $"Enriquecimiento fallido para proveedor '{proveedorNombre}': {enrichmentResult.ErrorMessage}. Dead-letter.",
                    eventId: eventId,
                    details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; error={enrichmentResult.ErrorMessage}");
                await SaveDeadLetterAsync(webhook, eventId, "error_enriquecimiento");
                return;
            }

            // 3c. Extraer routing key del payload enriquecido
            routingKeyResult = provider.ExtraerRoutingKey(enrichmentResult.PayloadEnriquecido!);
        }

        // ── Tramo común (order y payment): chequeo routing key, buscar caja, firmar, reenviar ──
        if (!routingKeyResult.EsValido)
        {
            _logger.LogWarning(
                "Routing key inválida para proveedor '{Proveedor}' tenant {TenantId}. Dead-letter.",
                proveedorNombre, webhook.TenantId);
            _systemLog.Warn(SystemLogCategory.Forward,
                $"Routing key inválida para proveedor '{proveedorNombre}'. Dead-letter.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; motivo=external_reference_invalida");
            await SaveDeadLetterAsync(webhook, eventId, "external_reference_invalida");
            return;
        }

        var identificadorCaja = routingKeyResult.RoutingKey!;

        // 5. Resolver caja en el padrón (cache-aside), distinguiendo "no existe" de "vencida".
        var resolucionCaja = await _cajaCache.ResolverAsync(webhook.TenantId, identificadorCaja);
        if (resolucionCaja.Estado != ResolucionCaja.Encontrada)
        {
            var (motivo, mensaje) = resolucionCaja.Estado == ResolucionCaja.Vencida
                ? ("caja_vencida",
                   $"Caja '{identificadorCaja}' registrada pero VENCIDA (último heartbeat {resolucionCaja.UltimaVez:O}). " +
                   "El ERP no se registró dentro del TTL. Dead-letter.")
                : ("caja_no_encontrada",
                   $"Caja '{identificadorCaja}' no encontrada en padrón. Dead-letter.");

            _logger.LogWarning(
                "Caja '{Identificador}' no ruteable para tenant {TenantId} (motivo={Motivo}). Dead-letter.",
                identificadorCaja, webhook.TenantId, motivo);
            _systemLog.Warn(SystemLogCategory.Worker,
                mensaje,
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; identificador={identificadorCaja}; motivo={motivo}; ultimaVez={resolucionCaja.UltimaVez:O}");
            await SaveDeadLetterAsync(webhook, eventId, motivo);
            return;
        }

        var caja = resolucionCaja.Caja!;

        // 6. Obtener WebhookSecret del tenant para firmar el callback.
        //    El tenant ya fue cargado con Include en el paso 2 — no se necesita un segundo scope.
        var webhookSecret = configEntidad.Tenant?.WebhookSecret ?? string.Empty;

        if (string.IsNullOrEmpty(webhookSecret))
        {
            _logger.LogError(
                "WebhookSecret ausente para tenant {TenantId}. Dead-letter sin forward.",
                webhook.TenantId);
            _systemLog.Error(SystemLogCategory.Forward,
                $"WebhookSecret ausente para tenant {webhook.TenantId}. Dead-letter.",
                eventId: eventId,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; motivo=secret_tenant_ausente");
            await SaveDeadLetterAsync(webhook, eventId, "secret_tenant_ausente");
            return;
        }

        // 7. Reenviar RAW a la CallbackUrl de la caja con X-Caja-Signature
        var callbackUrl = caja.CallbackUrl;
        var headers = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(webhookSecret))
        {
            var rawBytes = Encoding.UTF8.GetBytes(webhook.Payload);
            var secretBytes = Encoding.UTF8.GetBytes(webhookSecret);
            var hmacBytes = HMACSHA256.HashData(secretBytes, rawBytes);
            headers["X-Caja-Signature"] = Convert.ToHexString(hmacBytes).ToLowerInvariant();
        }

        var result = await ForwardWithCircuitBreakerAsync(callbackUrl, webhook.Payload, headers, ct);

        if (result.IsSuccess)
        {
            _systemLog.Info(SystemLogCategory.Forward,
                $"Webhook de proveedor '{proveedorNombre}' reenviado a caja '{identificadorCaja}' → HTTP {result.StatusCode}",
                eventId: eventId,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: callbackUrl,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; identificador={identificadorCaja}");

            // Se persisten los headers del callback (incluye X-Caja-Signature) y el nombre
            // del cliente para que los retries (auto y manual) usen "CajaCallback" (sin redirect).
            await SaveDeliveryLogAsync(webhook, result, eventId, attemptNumber: 1, currentStep: 0,
                DeliveryStatus.Success, targetUrl: callbackUrl, headersParaLog: headers,
                forwardClientName: "CajaCallback");
        }
        else
        {
            _systemLog.Error(SystemLogCategory.Forward,
                $"Reenvío a caja '{identificadorCaja}' falló: {result.ErrorMessage}",
                eventId: eventId,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: callbackUrl,
                responseBody: result.ResponseBody,
                details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; identificador={identificadorCaja}",
                ex: result.Exception);

            // Se persisten los headers del callback (incluye X-Caja-Signature) y el nombre
            // del cliente para que los retries (auto y manual) usen "CajaCallback" (sin redirect).
            await SaveDeliveryLogAsync(webhook, result, eventId, attemptNumber: 1, currentStep: 0,
                DeliveryStatus.Scheduled, targetUrl: callbackUrl, headersParaLog: headers,
                forwardClientName: "CajaCallback");
        }
    }

    // ─── Helpers de encolado ──────────────────────────────────────────────────

    /// <summary>
    /// Extrae el id del evento del payload (ej. data.id en MP, u otros campos top-level).
    /// Retorna string vacía si no se puede extraer (el proveedor gestionará el error).
    /// </summary>
    private static string ExtraerIdEvento(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            // MP: data.id
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("id", out var idProp))
            {
                return idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString() ?? string.Empty
                    : idProp.GetRawText();
            }
            // Fallback: id top-level
            if (doc.RootElement.TryGetProperty("id", out var topId))
            {
                return topId.ValueKind == JsonValueKind.String
                    ? topId.GetString() ?? string.Empty
                    : topId.GetRawText();
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ═══════════════════════════════════════════════════════
    // LOOP 2: Scheduler de reintentos
    // ═══════════════════════════════════════════════════════

    private async Task RetrySchedulerLoopAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessScheduledRetriesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en retry scheduler");
            }

            await Task.Delay(5000, ct);
        }
    }

    private async Task ProcessScheduledRetriesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var pendingRetries = await db.DeliveryLogs
            .Include(l => l.Tenant).ThenInclude(t => t!.RetryPolicy).ThenInclude(p => p!.Steps)
            .Where(l => l.Status == DeliveryStatus.Scheduled
                && l.NextRetryAt != null
                && l.NextRetryAt <= DateTime.UtcNow)
            .OrderBy(l => l.NextRetryAt)
            .Take(10)
            .ToListAsync(ct);

        foreach (var log in pendingRetries)
        {
            if (ct.IsCancellationRequested) break;
            await RetryScheduledLogAsync(db, log, ct);
        }
    }

    private async Task<List<RetryStep>> GetRetryStepsAsync(GatewayDbContext db, int tenantId)
    {
        var tenant = await db.Tenants
            .Include(t => t.RetryPolicy).ThenInclude(p => p!.Steps)
            .Include(t => t.TenantGroup).ThenInclude(g => g!.RetryPolicy).ThenInclude(p => p!.Steps)
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        var policy = tenant?.RetryPolicy;

        if (policy is null)
            policy = tenant?.TenantGroup?.RetryPolicy;

        if (policy is null)
        {
            policy = await db.RetryPolicies
                .Include(p => p.Steps)
                .FirstOrDefaultAsync(p => p.IsDefault);
        }

        return policy?.Steps.OrderBy(s => s.StepNumber).ToList() ?? [];
    }

    /// <summary>
    /// Punto de entrada testeable para el retry de un log programado. Public para acceso desde tests.
    /// </summary>
    public Task RetryScheduledLogParaTestAsync(GatewayDbContext db, DeliveryLog log, CancellationToken ct)
        => RetryScheduledLogAsync(db, log, ct);

    private async Task RetryScheduledLogAsync(GatewayDbContext db, DeliveryLog log, CancellationToken ct)
    {
        var tenant = log.Tenant;
        var targetUrl = log.TargetUrl ?? tenant.TargetUrl;
        var payload = log.Payload ?? string.Empty;

        _logger.LogInformation(
            "Reintento programado #{LogId} paso {Step} para '{Tenant}' → {Url}",
            log.Id, log.CurrentStep + 1, tenant.Name, targetUrl);

        var headers = DeserializeHeaders(log.ForwardedHeadersJson);
        var result = await _forwarder.ForwardAsync(targetUrl, payload, tenant.Slug, headers, log.ForwardClientName);

        log.AttemptNumber++;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryLogId = log.Id,
            AttemptNumber = log.AttemptNumber,
            HttpStatusCode = result.StatusCode,
            DurationMs = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            IsManual = false,
            CreatedAt = DateTime.UtcNow
        });

        if (result.IsSuccess)
        {
            log.Status = DeliveryStatus.Success;
            log.HttpStatusCode = result.StatusCode;
            log.DurationMs = result.DurationMs;
            log.NextRetryAt = null;
            log.ErrorMessage = null;

            _systemLog.Info(SystemLogCategory.Retry,
                $"Reintento #{log.Id} exitoso → HTTP {result.StatusCode} en {result.DurationMs}ms",
                tenantSlug: tenant.Slug,
                eventId: log.WebhookEventId,
                deliveryLogId: log.Id,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: targetUrl);
        }
        else
        {
            var nextStep = log.CurrentStep + 1;
            var steps = await GetRetryStepsAsync(db, tenant.Id);

            if (nextStep < steps.Count)
            {
                var delay = steps[nextStep].GetDelay();
                log.CurrentStep = nextStep;
                log.NextRetryAt = DateTime.UtcNow + delay;
                log.HttpStatusCode = result.StatusCode;
                log.DurationMs = result.DurationMs;
                log.ErrorMessage = result.ErrorMessage;

                _logger.LogWarning(
                    "Reintento #{LogId} falló (paso {Step}/{Max}). Próximo en {Delay}",
                    log.Id, nextStep + 1, steps.Count, delay);

                _systemLog.Warn(SystemLogCategory.Retry,
                    $"Reintento #{log.Id} falló (paso {nextStep + 1}/{steps.Count}). Próximo en {delay}",
                    tenantSlug: tenant.Slug,
                    eventId: log.WebhookEventId,
                    deliveryLogId: log.Id,
                    statusCode: result.StatusCode,
                    durationMs: result.DurationMs,
                    url: targetUrl,
                    responseBody: result.ResponseBody,
                    details: $"error={result.ErrorMessage}; nextRetryAt={log.NextRetryAt:O}");
            }
            else
            {
                log.Status = DeliveryStatus.Failed;
                log.NextRetryAt = null;
                log.HttpStatusCode = result.StatusCode;
                log.DurationMs = result.DurationMs;
                log.ErrorMessage = result.ErrorMessage;

                _logger.LogError(
                    "Webhook #{LogId} FALLÓ definitivamente tras {Attempts} intentos para '{Tenant}'",
                    log.Id, log.AttemptNumber, tenant.Name);

                _systemLog.Error(SystemLogCategory.Retry,
                    $"Webhook #{log.Id} FALLÓ definitivamente tras {log.AttemptNumber} intentos",
                    tenantSlug: tenant.Slug,
                    eventId: log.WebhookEventId,
                    deliveryLogId: log.Id,
                    statusCode: result.StatusCode,
                    durationMs: result.DurationMs,
                    url: targetUrl,
                    responseBody: result.ResponseBody,
                    details: $"error={result.ErrorMessage}; attempts={log.AttemptNumber}",
                    ex: result.Exception);
            }
        }

        await db.SaveChangesAsync(ct);
        await _monitor.NotifyChangeAsync();
    }

    // ═══════════════════════════════════════════════════════
    // Reenvío manual (sin pipeline, directo)
    // ═══════════════════════════════════════════════════════

    public async Task<ForwardResult> RetryManualAsync(long deliveryLogId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var log = await db.DeliveryLogs.Include(l => l.Tenant)
            .FirstOrDefaultAsync(l => l.Id == deliveryLogId);
        if (log is null)
            return new ForwardResult { IsSuccess = false, ErrorMessage = "Log no encontrado" };

        var targetUrl = log.TargetUrl ?? log.Tenant.TargetUrl;
        var payload = log.Payload ?? string.Empty;

        _logger.LogInformation("Reenvío manual #{LogId} → {Url}", deliveryLogId, targetUrl);
        _systemLog.Info(SystemLogCategory.ManualRetry,
            $"Reenvío manual #{deliveryLogId} → {targetUrl}",
            tenantSlug: log.Tenant.Slug,
            eventId: log.WebhookEventId,
            deliveryLogId: log.Id,
            url: targetUrl);

        var headers = DeserializeHeaders(log.ForwardedHeadersJson);
        var result = await _forwarder.ForwardAsync(targetUrl, payload, log.Tenant.Slug, headers, log.ForwardClientName);

        if (result.IsSuccess)
        {
            _systemLog.Info(SystemLogCategory.ManualRetry,
                $"Reenvío manual #{deliveryLogId} exitoso → HTTP {result.StatusCode} en {result.DurationMs}ms",
                tenantSlug: log.Tenant.Slug,
                eventId: log.WebhookEventId,
                deliveryLogId: log.Id,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: targetUrl);
        }
        else
        {
            _systemLog.Error(SystemLogCategory.ManualRetry,
                $"Reenvío manual #{deliveryLogId} falló: {result.ErrorMessage}",
                tenantSlug: log.Tenant.Slug,
                eventId: log.WebhookEventId,
                deliveryLogId: log.Id,
                statusCode: result.StatusCode,
                durationMs: result.DurationMs,
                url: targetUrl,
                responseBody: result.ResponseBody,
                ex: result.Exception);
        }

        log.AttemptNumber++;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryLogId = log.Id,
            AttemptNumber = log.AttemptNumber,
            HttpStatusCode = result.StatusCode,
            DurationMs = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            IsManual = true,
            CreatedAt = DateTime.UtcNow
        });

        log.HttpStatusCode = result.StatusCode;
        log.DurationMs = result.DurationMs;
        log.ErrorMessage = result.IsSuccess ? null : result.ErrorMessage;
        log.NextRetryAt = null;
        log.Status = result.IsSuccess ? DeliveryStatus.ManualRetry : DeliveryStatus.Failed;

        await db.SaveChangesAsync();
        await _monitor.NotifyChangeAsync();
        return result;
    }

    // ═══════════════════════════════════════════════════════
    // Circuit Breaker (keyed por callbackUrl con cap y TTL)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Sobrecarga para el flujo 1-a-1 (usa webhook.TargetUrl como clave del CB).
    /// </summary>
    private Task<ForwardResult> ForwardWithCircuitBreakerAsync(
        string targetUrl, QueuedWebhook webhook, CancellationToken ct)
    {
        var pipeline = ObtenerPipelineCb(targetUrl);
        return pipeline.ExecuteAsync(async _ =>
            await _forwarder.ForwardAsync(
                targetUrl,
                webhook.Payload,
                webhook.TenantSlug,
                webhook.ForwardedHeaders), ct).AsTask();
    }

    /// <summary>
    /// Sobrecarga para el flujo de proveedor (callbackUrl de la caja, headers inyectados).
    /// Usa el cliente "CajaCallback" (sin auto-redirect, anti-SSRF).
    /// </summary>
    private Task<ForwardResult> ForwardWithCircuitBreakerAsync(
        string callbackUrl,
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        var pipeline = ObtenerPipelineCb(callbackUrl);
        return pipeline.ExecuteAsync(async _ =>
            await _forwarder.ForwardAsync(callbackUrl, payload, string.Empty, headers, "CajaCallback"), ct).AsTask();
    }

    private ResiliencePipeline<ForwardResult> ObtenerPipelineCb(string callbackUrl)
    {
        // GetOrCreate es atómico respecto al check-then-set:
        // evita que dos hilos concurrentes creen instancias distintas para la misma URL
        // y pierdan el estado del circuit breaker.
        return _cbCache.GetOrCreate(callbackUrl, entry =>
        {
            entry.SetSlidingExpiration(CbTtl);
            entry.SetSize(1);
            return BuildCircuitBreaker();
        })!;
    }

    private static ResiliencePipeline<ForwardResult> BuildCircuitBreaker()
    {
        return new ResiliencePipelineBuilder<ForwardResult>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ForwardResult>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<ForwardResult>()
                    .HandleResult(r => !r.IsSuccess)
            }).Build();
    }

    /// <summary>Invalida el CB de una callbackUrl específica.</summary>
    public void InvalidatePipelineCache(string callbackUrl) => _cbCache.Remove(callbackUrl);

    /// <summary>
    /// No-op intencional. El dashboard llama este método al guardar/eliminar una retry policy,
    /// pero el CB pasó a estar keyed por callbackUrl con TTL deslizante (no por policyId).
    /// La configuración del CB no depende de la retry policy: editar una policy no invalida
    /// el estado de los circuit breakers de ningún tenant productivo.
    /// </summary>
    public void InvalidatePipelineCache(int policyId)
    {
        // No-op: el CB está keyed por callbackUrl, no por policyId.
        // Las ediciones de retry policy no afectan el estado del CB.
    }

    public void InvalidateAllPipelineCache() => _cbCache.Clear();

    // ═══════════════════════════════════════════════════════
    // Dead-letter
    // ═══════════════════════════════════════════════════════

    private async Task SaveDeadLetterAsync(QueuedWebhook webhook, Guid eventId, string motivo)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            db.DeliveryLogs.Add(new DeliveryLog
            {
                TenantId = webhook.TenantId,
                WebhookEventId = eventId,
                Payload = webhook.Payload?.Length > 50000
                    ? webhook.Payload[..50000] + "...[truncated]"
                    : webhook.Payload,
                SourceUrl = webhook.SourceUrl,
                TargetUrl = webhook.TargetUrl,
                HttpStatusCode = null,
                AttemptNumber = 1,
                DurationMs = 0,
                ErrorMessage = motivo,
                Status = DeliveryStatus.DeadLetter,
                CurrentStep = 0,
                NextRetryAt = null,
                CreatedAt = DateTime.UtcNow,
                Attempts =
                [
                    new DeliveryAttempt
                    {
                        AttemptNumber = 1,
                        HttpStatusCode = null,
                        DurationMs = 0,
                        ErrorMessage = motivo,
                        IsManual = false,
                        CreatedAt = DateTime.UtcNow
                    }
                ]
            });

            await db.SaveChangesAsync();
            await _monitor.NotifyChangeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error al guardar DeadLetter para evento {EventId}", eventId);
        }
    }

    // ═══════════════════════════════════════════════════════
    // Serialización de headers reenviados
    // ═══════════════════════════════════════════════════════

    private static string? SerializeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        return JsonSerializer.Serialize(headers);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════
    // Guardar log de primera entrega
    // ═══════════════════════════════════════════════════════

    private async Task SaveDeliveryLogAsync(
        QueuedWebhook webhook, ForwardResult result, Guid eventId,
        int attemptNumber, int currentStep, DeliveryStatus status,
        string? targetUrl = null,
        IReadOnlyDictionary<string, string>? headersParaLog = null,
        string? forwardClientName = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var finalTargetUrl = targetUrl ?? webhook.TargetUrl;

            DateTime? nextRetryAt = null;
            if (!result.IsSuccess && status != DeliveryStatus.DeadLetter)
            {
                var steps = await GetRetryStepsAsync(db, webhook.TenantId);

                if (steps.Count > 0)
                {
                    nextRetryAt = DateTime.UtcNow + steps[0].GetDelay();
                    status = DeliveryStatus.Scheduled;
                }
                else
                {
                    status = DeliveryStatus.Failed;
                }
            }

            // Si se proporcionan headersParaLog (flujo de proveedor), se persisten en lugar
            // de los headers del webhook entrante. Esto garantiza que retries (auto y manual)
            // reenvíen con X-Caja-Signature correcta. El secreto del tenant NO se persiste.
            var headersJson = headersParaLog is not null
                ? SerializeHeaders(headersParaLog)
                : SerializeHeaders(webhook.ForwardedHeaders);

            var deliveryLog = new DeliveryLog
            {
                TenantId = webhook.TenantId,
                WebhookEventId = eventId,
                Payload = webhook.Payload?.Length > 50000
                    ? webhook.Payload[..50000] + "...[truncated]"
                    : webhook.Payload,
                SourceUrl = webhook.SourceUrl,
                TargetUrl = finalTargetUrl,
                ForwardedHeadersJson = headersJson,
                ForwardClientName = forwardClientName,
                HttpStatusCode = result.StatusCode,
                AttemptNumber = attemptNumber,
                DurationMs = result.DurationMs,
                ErrorMessage = result.ErrorMessage,
                Status = status,
                CurrentStep = currentStep,
                NextRetryAt = nextRetryAt,
                CreatedAt = DateTime.UtcNow,
                Attempts =
                [
                    new DeliveryAttempt
                    {
                        AttemptNumber = attemptNumber,
                        HttpStatusCode = result.StatusCode,
                        DurationMs = result.DurationMs,
                        ErrorMessage = result.ErrorMessage,
                        IsManual = false,
                        CreatedAt = DateTime.UtcNow
                    }
                ]
            };
            db.DeliveryLogs.Add(deliveryLog);

            await db.SaveChangesAsync();
            await _monitor.NotifyChangeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar DeliveryLog para '{Slug}'", webhook.TenantSlug);
        }
    }
}
