namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Registro de una caja (punto de venta) de un tenant.
/// El identificador es OPACO: el ERP lo genera libremente, el gateway lo compara EXACTO.
/// No puede contener "__" (separador del external_reference de MercadoPago).
/// </summary>
public class CajaRegistrada
{
    public long Id { get; set; }

    /// <summary>FK al tenant propietario de esta caja.</summary>
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Identificador opaco de la caja. String arbitraria (puede tener guiones, etc.)
    /// sin "__" (esa restricción se valida en el endpoint). Max 100 chars.
    /// </summary>
    public string Identificador { get; set; } = string.Empty;

    /// <summary>URL HTTPS de callback donde se reenvían los webhooks enriquecidos. Max 2000 chars.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Último momento UTC en que la caja hizo un heartbeat (registro o re-registro).</summary>
    public DateTime? UltimaVez { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
