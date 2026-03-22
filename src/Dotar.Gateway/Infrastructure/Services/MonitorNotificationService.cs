namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Servicio singleton que notifica a los suscriptores (Monitor page)
/// cuando hay cambios en las entregas de webhooks (nuevo log, reenvío, etc).
/// Permite auto-refresh del Monitor en tiempo real.
/// </summary>
public class MonitorNotificationService
{
    public event Func<Task>? OnDeliveryChanged;

    /// <summary>
    /// Notifica que hubo un cambio en las entregas (nuevo log, reenvío manual, etc).
    /// </summary>
    public async Task NotifyChangeAsync()
    {
        if (OnDeliveryChanged is not null)
        {
            await OnDeliveryChanged.Invoke();
        }
    }
}
