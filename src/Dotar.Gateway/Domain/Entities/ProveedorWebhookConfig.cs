namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Configuración de un proveedor de webhooks (ej. MercadoPago) para un tenant.
/// Las credenciales (access token, signing secret) se almacenan cifradas como JSON.
/// No existe columna SecretProveedor plana; todo va en CredencialesCifradas.
/// </summary>
public class ProveedorWebhookConfig
{
    public long Id { get; set; }

    /// <summary>FK al tenant propietario de esta configuración.</summary>
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>Clave del proveedor (ej. "mercadopago"). Debe coincidir con IWebhookProvider.Nombre.</summary>
    public string ProveedorNombre { get; set; } = string.Empty;

    /// <summary>
    /// Id de la cuenta del tenant en el proveedor (ej. user_id de MP).
    /// Usado para el lookup inverso: (ProveedorNombre, CuentaExternaId) → Tenant.
    /// Max 100 chars.
    /// </summary>
    public string CuentaExternaId { get; set; } = string.Empty;

    /// <summary>
    /// JSON cifrado con IDataProtector que contiene las credenciales del proveedor
    /// (access token, signing secret, etc.). El cifrado real se aplica en WU-3 (AppService).
    /// </summary>
    public string CredencialesCifradas { get; set; } = string.Empty;

    /// <summary>Base URL de la API del proveedor (ej. "https://api.mercadopago.com").</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Si false, el proveedor está deshabilitado y no se procesa.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
