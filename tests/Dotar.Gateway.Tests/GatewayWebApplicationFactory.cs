using Dotar.Gateway.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dotar.Gateway.Tests;

/// <summary>
/// WebApplicationFactory que reemplaza SQLite por una DB temporal aislada
/// y desactiva los hosted services (TunnelStartupService, dispatcher) para
/// no requerir Redis ni Cloudflare en los tests.
/// </summary>
public class GatewayWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"gateway-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={DbPath}",
                ["ConnectionStrings:Redis"] = "localhost:0"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Quitar TODOS los hosted services para que no arranquen workers ni túnel.
            // Luego re-agregar solo SystemLogService para que persista logs a la DB
            // (necesario para los tests de integración de observabilidad).
            var hostedToRemove = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedToRemove) services.Remove(d);

            // Re-agregar SystemLogService como hosted service (ya está registrado como singleton).
            services.AddHostedService(sp => sp.GetRequiredService<SystemLogService>());
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort */ }
    }

}
