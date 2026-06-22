namespace Dotar.Gateway.Domain.Models;

/// <summary>
/// DTO que se serializa en Redis como mensaje pendiente de reenvío.
/// </summary>
public class QueuedWebhook
{
    public int TenantId { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Id de evento webhook único (correlación entre logs e intentos).</summary>
    public Guid EventId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Headers del webhook entrante que deben reenviarse verbatim al downstream
    /// (ya filtrados por HeaderForwardingPolicy). Clave = nombre exacto del header.
    /// </summary>
    public Dictionary<string, string> ForwardedHeaders { get; set; } = new();

    /// <summary>
    /// Nombre del proveedor de webhook si el tenant tiene ruteo por proveedor (ej. "mercadopago").
    /// Null para el flujo 1-a-1 clásico vía POST /ingest/{slug}.
    /// </summary>
    public string? ProveedorNombre { get; set; }
}
