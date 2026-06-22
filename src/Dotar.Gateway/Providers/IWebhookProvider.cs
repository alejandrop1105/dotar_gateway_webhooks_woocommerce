using Dotar.Gateway.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Dotar.Gateway.Providers;

/// <summary>
/// Resultado de extracción de routing key desde un payload enriquecido.
/// </summary>
public record RoutingKeyResult(bool EsValido, string? RoutingKey)
{
    /// <summary>Resultado válido con la routing key extraída.</summary>
    public static RoutingKeyResult Valido(string routingKey) => new(true, routingKey);

    /// <summary>Resultado inválido: no se pudo extraer la routing key. Trigger de dead-letter.</summary>
    public static RoutingKeyResult Invalid => new(false, null);
}

/// <summary>
/// Resultado del enriquecimiento: llamada a la API del proveedor para obtener más datos del evento.
/// </summary>
public record EnrichmentResult(bool Exitoso, string? PayloadEnriquecido, string? ErrorMessage = null)
{
    /// <summary>Enriquecimiento exitoso con el payload completo de la API del proveedor.</summary>
    public static EnrichmentResult Ok(string payload) => new(true, payload);

    /// <summary>Enriquecimiento fallido. Trigger de dead-letter.</summary>
    public static EnrichmentResult Fallo(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>
/// Abstracción de proveedor de webhooks. Cada implementación encapsula la lógica
/// específica del proveedor (MP, Stripe, etc.) y se registra con keyed DI.
/// Clave de registro = IWebhookProvider.Nombre (ej. "mercadopago").
/// </summary>
public interface IWebhookProvider
{
    /// <summary>Nombre del proveedor. Coincide con la clave de keyed DI y con ProveedorWebhookConfig.ProveedorNombre.</summary>
    string Nombre { get; }

    /// <summary>
    /// Extrae el identificador de cuenta externa del tenant desde el payload entrante (headers + body).
    /// MP: lee user_id del body JSON. Retorna null si no puede resolverse → 404 en el endpoint.
    /// </summary>
    string? ResolverCuentaExterna(IHeaderDictionary headers, byte[] body);

    /// <summary>
    /// Valida la firma entrante del webhook usando el SecretProveedor de la config.
    /// Debe ser timing-safe (CryptographicOperations.FixedTimeEquals).
    /// Retorna false si el header está ausente, malformado o la firma no coincide.
    /// </summary>
    bool ValidarFirmaEntrante(IHeaderDictionary headers, byte[] body, ProveedorWebhookConfig config);

    /// <summary>
    /// Enriquece el evento llamando a la API del proveedor con las credenciales del tenant.
    /// Retorna el JSON completo del recurso enriquecido, o EnrichmentResult.Fallo si hay error.
    /// </summary>
    Task<EnrichmentResult> EnriquecerAsync(string idEvento, ProveedorWebhookConfig config, CancellationToken ct);

    /// <summary>
    /// Extrae la routing key del payload enriquecido para localizar la caja de destino.
    /// Sin "::", parte izquierda vacía, o campo ausente → RoutingKeyResult.Invalid.
    /// </summary>
    RoutingKeyResult ExtraerRoutingKey(string payloadEnriquecido);
}
