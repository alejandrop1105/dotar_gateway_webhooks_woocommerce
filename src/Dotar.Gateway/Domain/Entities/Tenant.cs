using System.Text.RegularExpressions;

namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Representa un tenant (sistema origen de webhooks) registrado en el Gateway.
/// El slug se usa en la URL de ingesta: POST /ingest/{slug}
/// </summary>
public class Tenant
{
    // Patrón idéntico al de TenantApiEndpoints.cs:16 — fuente de verdad movida al dominio.
    // Permite: 1 char alfanumérico ó 2-100 chars alfanuméricos con guiones sin guión al inicio/final.
    private static readonly Regex SlugRegex = new(
        "^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$|^[a-z0-9]$",
        RegexOptions.Compiled);

    /// <summary>Normaliza un slug crudo a su forma canónica: trim + lowercase invariante.</summary>
    public static string NormalizeSlug(string raw) => raw.Trim().ToLowerInvariant();

    /// <summary>Valida que un slug YA normalizado cumpla el formato canónico del proyecto.</summary>
    public static bool IsValidSlug(string normalizedSlug) => SlugRegex.IsMatch(normalizedSlug);


    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>Esquema HMAC del sistema origen.</summary>
    public SignatureScheme SignatureScheme { get; set; } = SignatureScheme.WooCommerce;

    /// <summary>
    /// Header donde el origen envía la firma. Si null, se usa el header default del esquema.
    /// Útil principalmente para SignatureScheme.Generic.
    /// </summary>
    public string? SignatureHeader { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>FK a la política de reintento. Si null, usa la del grupo o la default.</summary>
    public int? RetryPolicyId { get; set; }
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>FK al grupo lógico del tenant (opcional).</summary>
    public int? TenantGroupId { get; set; }
    public TenantGroup? TenantGroup { get; set; }

    public ICollection<DeliveryLog> DeliveryLogs { get; set; } = [];

    // ─── Ruteo por proveedor (WooCommerce multi-sucursal) ─────────────────────

    /// <summary>
    /// Indica si el webhook entrante debe rutearse a través de un proveedor de
    /// ruteo dinámico (ej. woocommerce-multisucursal). Default false.
    /// </summary>
    public bool RuteoProveedorActivo { get; set; } = false;

    /// <summary>
    /// Nombre del proveedor de ruteo. Coincide con la clave de keyed DI.
    /// Ej. "woocommerce-multisucursal". Null si RuteoProveedorActivo == false.
    /// </summary>
    public string? ProveedorRuteoNombre { get; set; }

    /// <summary>
    /// Key del campo meta_data del pedido WooCommerce desde donde se extrae
    /// el identificador de sucursal. Configurable por tenant.
    /// </summary>
    public string? SucursalMetaKey { get; set; }

    /// <summary>
    /// Separador opcional dentro del value de meta_data. Si está configurado,
    /// se toma la parte izquierda del primer separador encontrado.
    /// </summary>
    public string? SucursalMetaSeparador { get; set; }
}
