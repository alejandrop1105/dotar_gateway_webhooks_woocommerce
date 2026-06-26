using Dotar.Gateway.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Dotar.Gateway.Providers;

/// <summary>
/// Provider de webhooks WooCommerce multi-sucursal.
/// Registrado con keyed DI como "woocommerce-multisucursal".
///
/// Lógica de ruteo:
///   - Siempre rutea sin enriquecimiento (no llama a la API de WooCommerce).
///   - Extrae la sucursal de destino desde meta_data[SucursalMetaKey] del payload.
///   - No requiere ProveedorWebhookConfig: la firma entrante ya fue validada por
///     IngestEndpoints usando el WebhookSecret del Tenant (HMAC-SHA256 base64,
///     header X-WC-Webhook-Signature).
///   - Sin HttpClient ni credenciales externas.
/// </summary>
public class WooCommerceMultiSucursalProvider : IWebhookProvider
{
    private readonly ILogger<WooCommerceMultiSucursalProvider> _logger;

    public WooCommerceMultiSucursalProvider(ILogger<WooCommerceMultiSucursalProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Nombre => "woocommerce-multisucursal";

    /// <inheritdoc/>
    /// WooCommerce no requiere ProveedorWebhookConfig — usa el WebhookSecret del Tenant.
    public bool RequiereConfigProveedor => false;

    /// <inheritdoc/>
    /// WooCommerce dead-letterea por "sucursal_ausente" cuando la routing key no es extraíble.
    public string MotivoRoutingKeyInvalida => "sucursal_ausente";

    /// <inheritdoc/>
    /// WooCommerce siempre rutea sin enriquecimiento: el payload RAW del pedido es suficiente.
    public bool RutearSinEnriquecimiento(string payloadNotificacion) => true;

    /// <inheritdoc/>
    /// Extrae la routing key usando SucursalMetaKey y SucursalMetaSeparador del tenant.
    /// Delega en SucursalMetaDataExtractor para mantener la lógica pura y testeable.
    public RoutingKeyResult ExtraerRoutingKeyConConfig(string payload, Tenant tenant)
    {
        if (string.IsNullOrEmpty(tenant.SucursalMetaKey))
        {
            _logger.LogWarning(
                "Tenant {TenantId} no tiene SucursalMetaKey configurada. Routing key inválida.",
                tenant.Id);
            return RoutingKeyResult.Invalid;
        }

        return SucursalMetaDataExtractor.Extraer(payload, tenant.SucursalMetaKey, tenant.SucursalMetaSeparador);
    }

    /// <inheritdoc/>
    /// WooCommerce resuelve el tenant por slug de URL — no usa CuentaExternaId.
    /// Este método no aplica para este proveedor.
    public string? ResolverCuentaExterna(IHeaderDictionary headers, byte[] body) => null;

    /// <inheritdoc/>
    /// La firma entrante (X-WC-Webhook-Signature) la valida IngestEndpoints, no el provider.
    /// Este método no debe ser llamado en el flujo normal de WooCommerce.
    public bool ValidarFirmaEntrante(IHeaderDictionary headers, byte[] body, ProveedorWebhookConfig config)
    {
        _logger.LogWarning(
            "ValidarFirmaEntrante fue llamado en WooCommerceMultiSucursalProvider — " +
            "la validación de firma debe hacerse en IngestEndpoints, no aquí.");
        return false;
    }

    /// <inheritdoc/>
    /// WooCommerce no enriquece: el payload RAW del pedido contiene toda la información necesaria.
    /// Retorna Fallo defensivo para evitar llamadas accidentales al flujo de enriquecimiento.
    public Task<EnrichmentResult> EnriquecerAsync(
        string idEvento, ProveedorWebhookConfig config, CancellationToken ct)
    {
        _logger.LogError(
            "EnriquecerAsync fue llamado en WooCommerceMultiSucursalProvider — " +
            "este proveedor siempre usa RutearSinEnriquecimiento=true. " +
            "Revisa la bifurcación en WebhookDispatcherWorker.");
        return Task.FromResult(
            EnrichmentResult.Fallo("WooCommerceMultiSucursalProvider no soporta enriquecimiento."));
    }

    /// <inheritdoc/>
    /// No aplica para este proveedor: la routing key se extrae con ExtraerRoutingKeyConConfig.
    public RoutingKeyResult ExtraerRoutingKey(string payloadEnriquecido)
    {
        _logger.LogWarning(
            "ExtraerRoutingKey fue llamado en WooCommerceMultiSucursalProvider — " +
            "usar ExtraerRoutingKeyConConfig(payload, tenant) en su lugar.");
        return RoutingKeyResult.Invalid;
    }

    /// <inheritdoc/>
    /// No aplica para este proveedor: WooCommerce no tiene el formato data.external_reference de MP.
    /// El flujo correcto es ExtraerRoutingKeyConConfig(payload, tenant).
    /// El default interface method llama este método, pero WooCommerce lo overridea — nunca debe llamarse.
    public RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payloadNotificacion)
    {
        _logger.LogWarning(
            "ExtraerRoutingKeyDesdeNotificacion fue llamado en WooCommerceMultiSucursalProvider — " +
            "usar ExtraerRoutingKeyConConfig(payload, tenant) en su lugar.");
        return RoutingKeyResult.Invalid;
    }
}
