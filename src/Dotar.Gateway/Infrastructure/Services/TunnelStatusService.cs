namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Servicio singleton que mantiene el estado del túnel Cloudflare
/// visible para todos los componentes Blazor.
/// </summary>
public class TunnelStatusService
{
    private string? _tunnelUrl;
    private string _status = "Sin configurar";
    private bool _isConnected;

    public event Action? OnStatusChanged;

    public string? TunnelUrl => _tunnelUrl;
    public string Status => _status;
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Actualiza el estado del túnel y notifica a los suscriptores.
    /// </summary>
    public void UpdateStatus(string status, string? tunnelUrl = null, bool isConnected = false)
    {
        _status = status;
        _tunnelUrl = tunnelUrl;
        _isConnected = isConnected;
        OnStatusChanged?.Invoke();
    }
}
