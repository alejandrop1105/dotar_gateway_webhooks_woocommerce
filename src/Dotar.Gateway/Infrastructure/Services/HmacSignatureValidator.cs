using System.Security.Cryptography;
using System.Text;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Valida la firma HMAC-SHA256 que WooCommerce envía en el header
/// X-WC-Webhook-Signature. Timing-safe comparison.
/// </summary>
public class HmacSignatureValidator
{
    /// <summary>
    /// Valida que la firma Base64 coincida con el HMAC-SHA256 del body.
    /// </summary>
    public bool Validate(string secret, byte[] body, string signatureBase64)
    {
        if (string.IsNullOrEmpty(secret) || body.Length == 0 || string.IsNullOrEmpty(signatureBase64))
            return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var computedHash = HMACSHA256.HashData(secretBytes, body);
        var computedBase64 = Convert.ToBase64String(computedHash);

        // Timing-safe comparison para evitar timing attacks
        var expected = Encoding.UTF8.GetBytes(computedBase64);
        var actual = Encoding.UTF8.GetBytes(signatureBase64);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
