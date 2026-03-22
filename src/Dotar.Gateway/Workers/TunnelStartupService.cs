using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Infrastructure.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Workers;

/// <summary>
/// Servicio que al iniciar la app busca credenciales Cloudflare
/// guardadas en la DB y conecta el túnel automáticamente.
/// </summary>
public class TunnelStartupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TunnelStatusService _tunnelStatus;
    private readonly ILogger<TunnelStartupService> _logger;

    public TunnelStartupService(
        IServiceScopeFactory scopeFactory,
        TunnelStatusService tunnelStatus,
        ILogger<TunnelStartupService> logger)
    {
        _scopeFactory = scopeFactory;
        _tunnelStatus = tunnelStatus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Esperar unos segundos para que la DB esté lista
        await Task.Delay(3000, stoppingToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var apiToken = await GetSettingAsync(db, "Cloudflare:ApiToken");
            var accountId = await GetSettingAsync(db, "Cloudflare:AccountId");
            var zoneId = await GetSettingAsync(db, "Cloudflare:ZoneId");
            var tunnelName = await GetSettingAsync(db, "Cloudflare:TunnelName") ?? "woocommerce-gateway";
            var domain = await GetSettingAsync(db, "Cloudflare:Domain") ?? "dotarsoluciones.com";

            // Si no hay credenciales, no intentar conectar
            if (string.IsNullOrWhiteSpace(apiToken) ||
                string.IsNullOrWhiteSpace(accountId) ||
                string.IsNullOrWhiteSpace(zoneId))
            {
                _logger.LogInformation("No hay credenciales Cloudflare configuradas. Túnel no iniciado.");
                _tunnelStatus.UpdateStatus("⚠️ Sin configurar — ir a Configuración");
                return;
            }

            _tunnelStatus.UpdateStatus("⏳ Conectando túnel automáticamente...");
            _logger.LogInformation("Credenciales Cloudflare encontradas. Iniciando túnel {TunnelName}.{Domain}...",
                tunnelName, domain);

            var config = new CloudflareConfig
            {
                TunnelName = tunnelName,
                Domain = domain,
                ApiToken = apiToken,
                AccountId = accountId,
                ZoneId = zoneId
            };

            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var tunnelLogger = loggerFactory.CreateLogger<CloudflareTunnelManager>();

            var manager = new CloudflareTunnelManager(5200, config, tunnelLogger);
            await manager.StartAsync();

            var tunnelUrl = $"https://{tunnelName}.{domain}";
            _tunnelStatus.UpdateStatus("Conectado", tunnelUrl, isConnected: true);
            _logger.LogInformation("✅ Túnel Cloudflare activo: {TunnelUrl}", tunnelUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al iniciar túnel Cloudflare automáticamente");
            _tunnelStatus.UpdateStatus($"❌ Error: {ex.Message}");
        }
    }

    private static async Task<string?> GetSettingAsync(GatewayDbContext db, string key)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }
}
