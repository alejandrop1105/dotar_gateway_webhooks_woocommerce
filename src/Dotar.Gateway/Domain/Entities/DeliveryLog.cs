namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Estados posibles de una entrega de webhook.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>Entregado exitosamente.</summary>
    Success,
    /// <summary>Falló definitivamente (agotó todos los reintentos).</summary>
    Failed,
    /// <summary>Pendiente de reintento programado.</summary>
    Scheduled,
    /// <summary>Reenvío manual exitoso.</summary>
    ManualRetry,
    /// <summary>Enviado a dead-letter: no pudo enrutarse (caja no encontrada, enriquecimiento fallido, routing key inválida).</summary>
    DeadLetter
}

/// <summary>
/// Registro de cada intento de entrega de un webhook al destino final.
/// Los intentos del mismo webhook se agrupan por WebhookEventId.
/// </summary>
public class DeliveryLog
{
    public long Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>
    /// Identificador único del evento webhook original.
    /// Todos los reintentos del mismo webhook comparten este ID.
    /// </summary>
    public Guid WebhookEventId { get; set; } = Guid.NewGuid();

    public string? Payload { get; set; }

    /// <summary>URL de origen del webhook (sitio que generó el evento).</summary>
    public string? SourceUrl { get; set; }

    public string? TargetUrl { get; set; }

    /// <summary>
    /// JSON con los headers del webhook entrante reenviados al destino.
    /// Persistido para que los retries (que leen desde DB, no desde Redis) los reenvíen también.
    /// </summary>
    public string? ForwardedHeadersJson { get; set; }

    public int? HttpStatusCode { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Failed;

    /// <summary>En qué paso del reintento está (0 = primer intento).</summary>
    public int CurrentStep { get; set; }

    /// <summary>Cuándo debe ejecutarse el próximo reintento.</summary>
    public DateTime? NextRetryAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Nombre del HttpClient usado en el primer forward (ej. "CajaCallback").
    /// Null en el flujo 1-a-1 (usa el default "GatewayForwarder").
    /// Persisted para que los retries (auto y manual) usen el mismo cliente.
    /// </summary>
    public string? ForwardClientName { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Historial de todos los intentos de entrega.</summary>
    public List<DeliveryAttempt> Attempts { get; set; } = [];
}
