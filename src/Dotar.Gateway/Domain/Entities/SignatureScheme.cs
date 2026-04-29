namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Esquemas de firma HMAC soportados por el Gateway.
/// El header esperado y el formato de la firma dependen del esquema.
/// </summary>
public enum SignatureScheme
{
    /// <summary>WooCommerce: header X-WC-Webhook-Signature, base64(HMAC-SHA256(body)).</summary>
    WooCommerce = 0,

    /// <summary>GitHub: header X-Hub-Signature-256, formato "sha256=&lt;hex&gt;".</summary>
    GitHub = 1,

    /// <summary>Genérico: header configurable por tenant, hex lowercase del HMAC-SHA256.</summary>
    Generic = 2,

    /// <summary>Sin validación de firma (no recomendado).</summary>
    None = 3
}
