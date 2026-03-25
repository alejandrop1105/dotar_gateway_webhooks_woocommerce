using System.Collections.Concurrent;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Domain.Models;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;

namespace Dotar.Gateway.Workers;

/// <summary>
/// Worker que consume webhooks de Redis y los reenvía al destino final.
/// Implementa un scheduler de reintentos basado en pasos configurables por política.
/// Cada paso define su propio delay (segundos a días).
/// </summary>
public class WebhookDispatcherWorker : BackgroundService
{
    private readonly RedisQueueService _queue;
    private readonly ForwardingService _forwarder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitorNotificationService _monitor;
    private readonly ILogger<WebhookDispatcherWorker> _logger;
    private readonly ConcurrentDictionary<int, ResiliencePipeline<ForwardResult>> _cbCache = new();

    public WebhookDispatcherWorker(
        RedisQueueService queue,
        ForwardingService forwarder,
        IServiceScopeFactory scopeFactory,
        MonitorNotificationService monitor,
        ILogger<WebhookDispatcherWorker> logger)
    {
        _queue = queue;
        _forwarder = forwarder;
        _scopeFactory = scopeFactory;
        _monitor = monitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDispatcherWorker iniciado.");

        // Dos loops en paralelo:
        // 1. Consumir cola Redis (nuevos webhooks)
        // 2. Scheduler de reintentos (ejecutar logs con NextRetryAt vencido)
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
        _logger.LogInformation("Procesando webhook para '{Slug}' → {Url}",
            webhook.TenantSlug, webhook.TargetUrl);

        var eventId = Guid.NewGuid();
        var result = await ForwardWithCircuitBreakerAsync(webhook.TenantId, webhook, ct);

        await SaveDeliveryLogAsync(webhook, result, eventId, attemptNumber: 1, currentStep: 0,
            result.IsSuccess ? DeliveryStatus.Success : DeliveryStatus.Scheduled);
    }

    // ═══════════════════════════════════════════════════════
    // LOOP 2: Scheduler de reintentos
    // ═══════════════════════════════════════════════════════

    private async Task RetrySchedulerLoopAsync(CancellationToken ct)
    {
        // Esperar un poco al inicio
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

            // Chequear cada 5 segundos
            await Task.Delay(5000, ct);
        }
    }

    private async Task ProcessScheduledRetriesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // Buscar todos los logs Scheduled con NextRetryAt vencido
        var pendingRetries = await db.DeliveryLogs
            .Include(l => l.Tenant).ThenInclude(t => t!.RetryPolicy).ThenInclude(p => p!.Steps)
            .Where(l => l.Status == DeliveryStatus.Scheduled
                && l.NextRetryAt != null
                && l.NextRetryAt <= DateTime.UtcNow)
            .OrderBy(l => l.NextRetryAt)
            .Take(10) // Procesar en lotes de 10
            .ToListAsync(ct);

        foreach (var log in pendingRetries)
        {
            if (ct.IsCancellationRequested) break;
            await RetryScheduledLogAsync(db, log, ct);
        }
    }

    /// <summary>
    /// Obtiene los pasos de reintento para un tenant.
    /// Prioridad: política propia → política del grupo → política por defecto (IsDefault).
    /// </summary>
    private async Task<List<RetryStep>> GetRetryStepsAsync(GatewayDbContext db, int tenantId)
    {
        var tenant = await db.Tenants
            .Include(t => t.RetryPolicy).ThenInclude(p => p!.Steps)
            .Include(t => t.TenantGroup).ThenInclude(g => g!.RetryPolicy).ThenInclude(p => p!.Steps)
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        // 1. Política propia del tenant
        var policy = tenant?.RetryPolicy;

        // 2. Fallback: política del grupo
        if (policy is null)
            policy = tenant?.TenantGroup?.RetryPolicy;

        // 3. Fallback: política por defecto del sistema
        if (policy is null)
        {
            policy = await db.RetryPolicies
                .Include(p => p.Steps)
                .FirstOrDefaultAsync(p => p.IsDefault);
        }

        return policy?.Steps.OrderBy(s => s.StepNumber).ToList() ?? [];
    }

    private async Task RetryScheduledLogAsync(GatewayDbContext db, DeliveryLog log, CancellationToken ct)
    {
        var tenant = log.Tenant;
        var targetUrl = log.TargetUrl ?? tenant.TargetUrl;
        var payload = log.Payload ?? string.Empty;

        _logger.LogInformation(
            "Reintento programado #{LogId} paso {Step} para '{Tenant}' → {Url}",
            log.Id, log.CurrentStep + 1, tenant.Name, targetUrl);

        var result = await _forwarder.ForwardAsync(targetUrl, payload, tenant.Slug);

        // Registrar intento en historial
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
        }
        else
        {
            var nextStep = log.CurrentStep + 1;
            // Usar helper con fallback a política default
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
        var result = await _forwarder.ForwardAsync(targetUrl, payload, log.Tenant.Slug);

        // Registrar intento manual en historial
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

        // Actualizar estado maestro
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
    // Circuit Breaker (por política, no maneja pasos)
    // ═══════════════════════════════════════════════════════

    private async Task<ForwardResult> ForwardWithCircuitBreakerAsync(
        int tenantId, QueuedWebhook webhook, CancellationToken ct)
    {
        var policyId = await GetPolicyIdForTenantAsync(tenantId);
        var pipeline = _cbCache.GetOrAdd(policyId, _ => BuildCircuitBreaker(policyId));
        return await pipeline.ExecuteAsync(async _ =>
            await _forwarder.ForwardAsync(webhook.TargetUrl, webhook.Payload, webhook.TenantSlug), ct);
    }

    private ResiliencePipeline<ForwardResult> BuildCircuitBreaker(int policyId)
    {
        // Solo CB, no retry (los pasos los maneja el scheduler)
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

    private async Task<int> GetPolicyIdForTenantAsync(int tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.FindAsync(tenantId);
        return tenant?.RetryPolicyId ?? 0;
    }

    public void InvalidatePipelineCache(int policyId) => _cbCache.TryRemove(policyId, out _);
    public void InvalidateAllPipelineCache() => _cbCache.Clear();

    // ═══════════════════════════════════════════════════════
    // Guardar log de primera entrega
    // ═══════════════════════════════════════════════════════

    private async Task SaveDeliveryLogAsync(
        QueuedWebhook webhook, ForwardResult result, Guid eventId,
        int attemptNumber, int currentStep, DeliveryStatus status)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            DateTime? nextRetryAt = null;
            if (!result.IsSuccess)
            {
                // Buscar pasos con fallback a política default
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

            var deliveryLog = new DeliveryLog
            {
                TenantId = webhook.TenantId,
                WebhookEventId = eventId,
                Payload = webhook.Payload.Length > 50000
                    ? webhook.Payload[..50000] + "...[truncated]"
                    : webhook.Payload,
                SourceUrl = webhook.SourceUrl,
                TargetUrl = webhook.TargetUrl,
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
