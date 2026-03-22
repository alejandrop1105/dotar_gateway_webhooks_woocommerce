namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Registro individual de cada intento de entrega de un webhook.
/// Múltiples intentos pertenecen a un mismo DeliveryLog (maestro).
/// </summary>
public class DeliveryAttempt
{
    public long Id { get; set; }
    public long DeliveryLogId { get; set; }

    /// <summary>Número secuencial del intento (1, 2, 3...).</summary>
    public int AttemptNumber { get; set; }

    public int? HttpStatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True si fue disparado manualmente desde el Dashboard.</summary>
    public bool IsManual { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DeliveryLog DeliveryLog { get; set; } = null!;
}
