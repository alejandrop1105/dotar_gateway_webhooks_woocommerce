namespace Dotar.Gateway.Infrastructure.Tunnel;

/// <summary>
/// Configuración necesaria para provisionar y ejecutar un Named Tunnel
/// de Cloudflare vía API REST. Valores cargados desde appsettings.json.
/// Adaptado de WebHooks.LegacyEngine (net48) a .NET 9.
/// </summary>
public class CloudflareConfig
{
    /// <summary>
    /// Nombre del túnel/subdominio. Ej: "webhooks-gateway"
    /// </summary>
    public string TunnelName { get; set; } = string.Empty;

    /// <summary>
    /// Dominio base gestionado en Cloudflare. Ej: "dotarsoluciones.com"
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// API Token de Cloudflare con permisos Tunnel:Edit + DNS:Edit.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Account ID de Cloudflare.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// Zone ID de la zona DNS del dominio en Cloudflare.
    /// </summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Dominio limpio sin protocolo ni barras.
    /// </summary>
    public string CleanDomain
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Domain)) return string.Empty;
            var clean = Domain.Trim();
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                clean = clean[8..];
            else if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                clean = clean[7..];
            return clean.TrimEnd('/');
        }
    }

    /// <summary>
    /// Hostname completo calculado. Ej: "webhooks-gateway.dotarsoluciones.com"
    /// </summary>
    public string Hostname => $"{TunnelName}.{CleanDomain}";

    /// <summary>
    /// Valida que todos los campos obligatorios estén completos.
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(TunnelName) &&
        !string.IsNullOrWhiteSpace(Domain) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(ZoneId);
}
