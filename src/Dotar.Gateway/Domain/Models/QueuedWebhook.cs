namespace Dotar.Gateway.Domain.Models;

/// <summary>
/// DTO que se serializa en Redis como mensaje pendiente de reenvío.
/// </summary>
public class QueuedWebhook
{
    public int TenantId { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
