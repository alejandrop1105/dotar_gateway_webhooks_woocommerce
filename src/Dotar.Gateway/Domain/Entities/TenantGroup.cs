namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Agrupación lógica de tenants para administración grupal.
/// Permite asignar políticas de reintento a nivel de grupo.
/// </summary>
public class TenantGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Política de reintento grupal. Los tenants sin política propia heredan esta.</summary>
    public int? RetryPolicyId { get; set; }
    public RetryPolicy? RetryPolicy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Tenant> Tenants { get; set; } = [];
}
