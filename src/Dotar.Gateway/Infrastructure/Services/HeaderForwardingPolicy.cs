namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Decide qué headers entrantes se reenvían al destino final, preservando el nombre exacto.
///
/// Política:
///  - Reenvía verbatim todo header que arranque con "X-" salvo blacklist de transporte/proxy.
///  - Captura User-Agent original y lo expone como X-Original-User-Agent.
///  - No reenvía headers Hop-by-Hop, ni de Cloudflare, ni de proxies (X-Forwarded-*, Cf-*, Cdn-Loop, X-Real-IP).
///  - No reenvía Host/Connection/Content-Length/Content-Type (los gestiona la stack HTTP saliente).
///
/// Por qué importa: providers como WooCommerce (X-WC-Webhook-Topic, X-WC-Webhook-Signature,
/// X-WC-Webhook-Delivery-ID, etc.), MercadoPago (X-Signature, X-Request-Id), VTEX (X-Webhook-Secret)
/// dependen de estos headers para que el downstream pueda decidir acción, validar HMAC y deduplicar.
/// </summary>
public static class HeaderForwardingPolicy
{
    public const string OriginalUserAgentHeader = "X-Original-User-Agent";

    /// <summary>Headers exactos a NO reenviar (case-insensitive).</summary>
    private static readonly HashSet<string> ExactBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Transport / hop-by-hop
        "Host", "Connection", "Content-Length", "Content-Type",
        "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection", "TE", "Trailer",
        // Proxy / CDN sentinel
        "X-Real-IP", "Cdn-Loop", "Forwarded",
    };

    /// <summary>Prefijos a NO reenviar (case-insensitive).</summary>
    private static readonly string[] PrefixBlacklist =
    [
        "X-Forwarded-",
        "Cf-",                  // Cloudflare meta (Cf-Ray, Cf-Connecting-IP, Cf-Visitor, ...)
        ":",                    // HTTP/2 pseudo-headers
    ];

    /// <summary>True si el header debe reenviarse al downstream con su nombre exacto.</summary>
    public static bool ShouldForward(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName)) return false;
        if (ExactBlacklist.Contains(headerName)) return false;

        foreach (var prefix in PrefixBlacklist)
        {
            if (headerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Solo reenviamos headers X-* (los específicos del provider). User-Agent se maneja aparte.
        return headerName.StartsWith("X-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filtra un set de headers entrantes y produce el dict que el downstream debe recibir.
    /// Preserva nombre exacto y unifica multi-valor con coma (HTTP/1.1 RFC 7230 §3.2.2).
    /// </summary>
    public static Dictionary<string, string> SelectForwardable(
        IEnumerable<KeyValuePair<string, string[]>> incomingHeaders,
        string? originalUserAgent = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in incomingHeaders)
        {
            if (!ShouldForward(kv.Key)) continue;
            if (kv.Value is null || kv.Value.Length == 0) continue;

            var value = kv.Value.Length == 1 ? kv.Value[0] : string.Join(",", kv.Value);
            // Preservar el nombre exacto recibido (la primera vez que aparece)
            if (!result.ContainsKey(kv.Key))
                result[kv.Key] = value;
        }

        if (!string.IsNullOrWhiteSpace(originalUserAgent))
            result[OriginalUserAgentHeader] = originalUserAgent;

        return result;
    }
}
