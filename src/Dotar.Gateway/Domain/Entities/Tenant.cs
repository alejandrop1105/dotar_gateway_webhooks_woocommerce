namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Representa un tenant (instancia WooCommerce) registrado en el Gateway.
/// El slug se usa en la URL de ingesta: POST /ingest/{slug}
/// </summary>
public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>FK a la política de reintento. Si null, usa la del grupo o la default.</summary>
    public int? RetryPolicyId { get; set; }
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>FK al grupo lógico del tenant (opcional).</summary>
    public int? TenantGroupId { get; set; }
    public TenantGroup? TenantGroup { get; set; }

    public ICollection<DeliveryLog> DeliveryLogs { get; set; } = [];
}
