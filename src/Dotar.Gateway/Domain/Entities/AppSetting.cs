namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Configuración clave-valor persistida en SQLite.
/// Se usa para credenciales Cloudflare y otras settings del Gateway.
/// </summary>
public class AppSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
