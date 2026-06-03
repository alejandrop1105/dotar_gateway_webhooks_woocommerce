using System.Threading.Channels;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Escritor asíncrono de logs estructurados a SQLite.
/// Encolar es no-bloqueante (Channel ilimitado en memoria); un worker singleton hace
/// flush en lotes a la DB para no penalizar el path crítico de ingesta/forward.
/// </summary>
public class SystemLogService : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private const int MaxResponseBodyLength = 8000;
    private const int MaxDetailsLength = 8000;

    private readonly Channel<SystemLog> _channel = Channel.CreateUnbounded<SystemLog>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemLogService> _logger;

    public SystemLogService(IServiceScopeFactory scopeFactory, ILogger<SystemLogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Log(
        SystemLogLevel level,
        SystemLogCategory category,
        string message,
        string? tenantSlug = null,
        Guid? webhookEventId = null,
        long? deliveryLogId = null,
        int? httpStatusCode = null,
        long? durationMs = null,
        string? url = null,
        string? responseBody = null,
        string? details = null,
        Exception? exception = null)
    {
        var entry = new SystemLog
        {
            CreatedAt = DateTime.UtcNow,
            Level = level,
            Category = category,
            Message = Truncate(message, 2000),
            TenantSlug = tenantSlug,
            WebhookEventId = webhookEventId,
            DeliveryLogId = deliveryLogId,
            HttpStatusCode = httpStatusCode,
            DurationMs = durationMs,
            Url = Truncate(url, 2000),
            ResponseBody = Truncate(responseBody, MaxResponseBodyLength),
            Details = Truncate(details, MaxDetailsLength),
            Exception = exception is null ? null : Truncate(exception.ToString(), MaxDetailsLength)
        };

        if (!_channel.Writer.TryWrite(entry))
        {
            _logger.LogWarning("No se pudo encolar SystemLog: {Message}", message);
        }
    }

    public void Info(SystemLogCategory cat, string msg, string? tenantSlug = null, Guid? eventId = null,
        long? deliveryLogId = null, int? statusCode = null, long? durationMs = null,
        string? url = null, string? details = null)
        => Log(SystemLogLevel.Information, cat, msg, tenantSlug, eventId, deliveryLogId, statusCode, durationMs, url, details: details);

    public void Warn(SystemLogCategory cat, string msg, string? tenantSlug = null, Guid? eventId = null,
        long? deliveryLogId = null, int? statusCode = null, long? durationMs = null,
        string? url = null, string? responseBody = null, string? details = null)
        => Log(SystemLogLevel.Warning, cat, msg, tenantSlug, eventId, deliveryLogId, statusCode, durationMs, url, responseBody, details);

    public void Error(SystemLogCategory cat, string msg, string? tenantSlug = null, Guid? eventId = null,
        long? deliveryLogId = null, int? statusCode = null, long? durationMs = null,
        string? url = null, string? responseBody = null, string? details = null, Exception? ex = null)
        => Log(SystemLogLevel.Error, cat, msg, tenantSlug, eventId, deliveryLogId, statusCode, durationMs, url, responseBody, details, ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<SystemLog>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Esperar el primer ítem (o cancelación)
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken))
                    break;

                // Drenar hasta BatchSize o esperar hasta FlushInterval
                buffer.Clear();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(FlushInterval);

                while (buffer.Count < BatchSize)
                {
                    if (_channel.Reader.TryRead(out var item))
                    {
                        buffer.Add(item);
                        continue;
                    }
                    try
                    {
                        if (!await _channel.Reader.WaitToReadAsync(cts.Token))
                            break;
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        break; // timeout de flush
                    }
                }

                if (buffer.Count == 0) continue;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                db.SystemLogs.AddRange(buffer);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al persistir batch de SystemLogs ({Count} pendientes)", buffer.Count);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…[trunc]";
    }
}
