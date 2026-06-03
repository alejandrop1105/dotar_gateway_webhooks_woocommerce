namespace Dotar.Gateway.Domain.Entities;

public enum SystemLogLevel
{
    Information,
    Warning,
    Error
}

public enum SystemLogCategory
{
    Ingest,
    Forward,
    Retry,
    ManualRetry,
    Tunnel,
    Api,
    Auth,
    Worker,
    System
}

/// <summary>
/// Registro estructurado de operaciones del Gateway (ingesta, reenvío, reintentos, túnel, API, etc.).
/// Pensado para diagnosticar por qué un evento no llega al destino: incluye URL, HTTP code,
/// cuerpo de respuesta truncado y excepción cuando aplica.
/// </summary>
public class SystemLog
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public SystemLogLevel Level { get; set; }
    public SystemLogCategory Category { get; set; }

    /// <summary>Mensaje resumen (una línea).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Slug del tenant relacionado (si aplica).</summary>
    public string? TenantSlug { get; set; }

    /// <summary>Id del evento webhook para correlacionar logs de la misma entrega.</summary>
    public Guid? WebhookEventId { get; set; }

    /// <summary>Id del DeliveryLog asociado (si aplica).</summary>
    public long? DeliveryLogId { get; set; }

    /// <summary>HTTP status code devuelto por el destino (si aplica).</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>Duración total de la operación en ms.</summary>
    public long? DurationMs { get; set; }

    /// <summary>URL involucrada (target del reenvío, endpoint, etc.).</summary>
    public string? Url { get; set; }

    /// <summary>Cuerpo de la respuesta truncado (clave para diagnosticar 404/4xx/5xx del destino).</summary>
    public string? ResponseBody { get; set; }

    /// <summary>Detalles adicionales (JSON o texto libre) para contexto extra: headers, payload truncado, stack trace.</summary>
    public string? Details { get; set; }

    /// <summary>Mensaje de excepción (si aplica).</summary>
    public string? Exception { get; set; }
}
