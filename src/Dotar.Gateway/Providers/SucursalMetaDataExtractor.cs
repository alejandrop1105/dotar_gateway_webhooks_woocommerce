using System.Text.Json;

namespace Dotar.Gateway.Providers;

/// <summary>
/// Extractor estático y puro de routing key desde el campo meta_data de un payload WooCommerce.
/// Sin I/O, sin dependencias externas — testeable directamente por unidad.
///
/// Reglas de extracción:
///   - Retorna Invalid si: JSON inválido, meta_data ausente o no es array,
///     ningún item tiene key == metaKey, o el value resultante está vacío.
///   - Si separador no es null/vacío: value.Split(separador)[0].Trim()
///   - Si separador es null/vacío: value.Trim() completo
/// </summary>
public static class SucursalMetaDataExtractor
{
    /// <summary>
    /// Extrae la routing key del campo meta_data del payload WooCommerce.
    /// </summary>
    /// <param name="payload">JSON del pedido WooCommerce (string crudo).</param>
    /// <param name="metaKey">Key a buscar dentro del array meta_data.</param>
    /// <param name="separador">
    ///   Si no es null/vacío, se usa como separador: se toma la parte izquierda del primer split.
    ///   Si es null o vacío, se usa el value completo.
    /// </param>
    /// <returns>RoutingKeyResult.Valido con la routing key, o RoutingKeyResult.Invalid.</returns>
    public static RoutingKeyResult Extraer(string payload, string metaKey, string? separador)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // meta_data debe existir y ser un array
            if (!root.TryGetProperty("meta_data", out var metaDataProp))
                return RoutingKeyResult.Invalid;

            if (metaDataProp.ValueKind != JsonValueKind.Array)
                return RoutingKeyResult.Invalid;

            // Buscar el primer item cuya "key" coincida con metaKey
            string? value = null;
            foreach (var item in metaDataProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (!item.TryGetProperty("key", out var keyProp))
                    continue;

                if (keyProp.GetString() != metaKey)
                    continue;

                if (!item.TryGetProperty("value", out var valueProp))
                    continue;

                value = valueProp.GetString();
                break;
            }

            if (value is null)
                return RoutingKeyResult.Invalid;

            // Aplicar separador o usar value completo
            string routingKey;
            if (!string.IsNullOrEmpty(separador))
            {
                // Toma la parte izquierda del primer separador (índice 0 del split)
                routingKey = value.Split(separador)[0].Trim();
            }
            else
            {
                routingKey = value.Trim();
            }

            if (string.IsNullOrEmpty(routingKey))
                return RoutingKeyResult.Invalid;

            return RoutingKeyResult.Valido(routingKey);
        }
        catch (JsonException)
        {
            return RoutingKeyResult.Invalid;
        }
    }
}
