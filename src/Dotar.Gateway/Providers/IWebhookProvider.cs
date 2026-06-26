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
    /// Indica si este proveedor requiere una ProveedorWebhookConfig con credenciales cifradas.
    /// MercadoPago: true (necesita AccessToken + SigningSecret).
    /// WooCommerceMultiSucursal: false (usa WebhookSecret del Tenant directamente).
    /// </summary>
    bool RequiereConfigProveedor { get; }

    /// <summary>
    /// Motivo de dead-letter cuando la routing key no puede extraerse del payload.
    /// Default: "external_reference_invalida" (contrato histórico de MercadoPago).
    /// WooCommerceMultiSucursalProvider overridea con "sucursal_ausente".
    /// </summary>
    string MotivoRoutingKeyInvalida => "external_reference_invalida";

    /// <summary>
    /// Extrae la routing key usando la configuración del tenant (key y separador de meta_data).
    /// Método default: delega en ExtraerRoutingKeyDesdeNotificacion(payload) — sin cambios para
    /// MercadoPago que no usa configuración de tenant para extraer la routing key.
    /// WooCommerceMultiSucursalProvider overridea este método para leer SucursalMetaKey/SucursalMetaSeparador.
    /// </summary>
    RoutingKeyResult ExtraerRoutingKeyConConfig(string payload, Tenant tenant)
        => ExtraerRoutingKeyDesdeNotificacion(payload);

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
    /// Sin "__", parte izquierda vacía, o campo ausente → RoutingKeyResult.Invalid.
    /// </summary>
    RoutingKeyResult ExtraerRoutingKey(string payloadEnriquecido);

    /// <summary>
    /// Indica si la notificación debe rutearse sin enriquecimiento (flujo directo).
    /// MP: retorna true si el campo top-level "type" es "order" (OrdinalIgnoreCase).
    /// JSON inválido o campo ausente → false (conservador; preserva el flujo payment).
    /// </summary>
    bool RutearSinEnriquecimiento(string payloadNotificacion);

    /// <summary>
    /// Extrae la routing key directamente desde el payload RAW de la notificación
    /// (para el flujo order, sin enriquecimiento). MP: lee data.external_reference (campo anidado).
    /// Aplica Split("__", 2)[0]. Mismas reglas de validación que ExtraerRoutingKey.
    /// </summary>
    RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payloadNotificacion);
}
