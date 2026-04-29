using System.Security.Cryptography;
using System.Text;
using Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Valida firmas HMAC-SHA256 de webhooks entrantes con soporte multi-esquema:
/// WooCommerce (base64), GitHub (sha256=hex), Generic (hex), None.
/// Comparación timing-safe.
/// </summary>
public class HmacSignatureValidator
{
    public const string WooCommerceHeader = "X-WC-Webhook-Signature";
    public const string GitHubHeader = "X-Hub-Signature-256";
    public const string GenericHeaderDefault = "X-Webhook-Signature";

    /// <summary>Header donde se espera la firma según el esquema y el override del tenant.</summary>
    public static string ResolveHeader(SignatureScheme scheme, string? tenantHeaderOverride)
    {
        if (!string.IsNullOrWhiteSpace(tenantHeaderOverride))
            return tenantHeaderOverride;

        return scheme switch
        {
            SignatureScheme.WooCommerce => WooCommerceHeader,
            SignatureScheme.GitHub => GitHubHeader,
            SignatureScheme.Generic => GenericHeaderDefault,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Valida la firma del body según el esquema configurado por el tenant.
    /// </summary>
    public bool Validate(SignatureScheme scheme, string secret, byte[] body, string? signature)
    {
        if (scheme == SignatureScheme.None)
            return true;

        if (string.IsNullOrEmpty(secret) || body.Length == 0 || string.IsNullOrEmpty(signature))
            return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var computedHash = HMACSHA256.HashData(secretBytes, body);

        return scheme switch
        {
            SignatureScheme.WooCommerce => CompareBase64(computedHash, signature),
            SignatureScheme.GitHub => CompareGitHubHex(computedHash, signature),
            SignatureScheme.Generic => CompareHex(computedHash, signature),
            _ => false
        };
    }

    private static bool CompareBase64(byte[] computed, string signature)
    {
        var computedBase64 = Convert.ToBase64String(computed);
        var expected = Encoding.UTF8.GetBytes(computedBase64);
        var actual = Encoding.UTF8.GetBytes(signature);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool CompareGitHubHex(byte[] computed, string signature)
    {
        const string prefix = "sha256=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return CompareHex(computed, signature[prefix.Length..]);
    }

    private static bool CompareHex(byte[] computed, string hexSignature)
    {
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();
        var expected = Encoding.UTF8.GetBytes(computedHex);
        var actual = Encoding.UTF8.GetBytes(hexSignature.ToLowerInvariant());
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
