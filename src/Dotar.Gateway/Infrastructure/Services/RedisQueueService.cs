using System.Text.Json;
using Dotar.Gateway.Domain.Models;
using StackExchange.Redis;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Cola de mensajes en Redis usando RPUSH/BLPOP para procesamiento FIFO.
/// </summary>
public class RedisQueueService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly string _queueKey;
    private readonly ILogger<RedisQueueService> _logger;

    public RedisQueueService(
        IConnectionMultiplexer? redis,
        IConfiguration configuration,
        ILogger<RedisQueueService> logger)
    {
        _redis = redis;
        _queueKey = configuration.GetValue<string>("Gateway:QueueKey") ?? "gateway:webhooks";
        _logger = logger;
    }

    private IDatabase GetDb() =>
        (_redis ?? throw new InvalidOperationException("Redis no está configurado.")).GetDatabase();

    /// <summary>
    /// Encola un webhook para procesamiento asíncrono.
    /// </summary>
    public virtual async Task EnqueueAsync(QueuedWebhook webhook)
    {
        var json = JsonSerializer.Serialize(webhook);
        await GetDb().ListRightPushAsync(_queueKey, json);
        _logger.LogDebug("Webhook encolado para tenant {Slug}", webhook.TenantSlug);
    }

    /// <summary>
    /// Desencola el siguiente webhook (blocking pop con timeout).
    /// </summary>
    public virtual async Task<QueuedWebhook?> DequeueAsync(int timeoutSeconds = 5)
    {
        var result = await GetDb().ListLeftPopAsync(_queueKey);
        if (result.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<QueuedWebhook>(result!);
    }

    /// <summary>
    /// Retorna la cantidad de mensajes pendientes en la cola.
    /// </summary>
    public virtual async Task<long> GetPendingCountAsync()
    {
        return await GetDb().ListLengthAsync(_queueKey);
    }
}
