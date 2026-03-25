using Dotar.Gateway.Endpoints;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ─── Configurar URLs ──────────────────────────────────
builder.WebHost.UseUrls("http://0.0.0.0:5200");



// ─── SQLite + EF Core (WAL mode) ─────────────────────
builder.Services.AddDbContext<GatewayDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Sqlite")
        ?? "Data Source=gateway.db";
    options.UseSqlite(connectionString);
    options.ConfigureWarnings(w =>
        w.Log(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// ─── Redis (lazy connect — no crash si no está disponible) ───
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6380";
    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

// ─── Servicios de Infraestructura ─────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<HmacSignatureValidator>();
builder.Services.AddSingleton<RedisQueueService>();
builder.Services.AddSingleton<TenantCacheService>();
builder.Services.AddSingleton<TunnelStatusService>();
builder.Services.AddSingleton<MonitorNotificationService>();
builder.Services.AddTransient<ForwardingService>();

// ─── HttpClientFactory para reenvío ───────────────────
builder.Services.AddHttpClient("GatewayForwarder", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "DotarGateway/1.0");
});

// ─── Worker Background Services ──────────────────────
// Worker como singleton para que el Monitor pueda invocarlo para retry manual
builder.Services.AddSingleton<WebhookDispatcherWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcherWorker>());
builder.Services.AddHostedService<TunnelStartupService>();

// ─── Blazor Server (Interactive) + MudBlazor ─────
builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ─── Aplicar Migraciones EF Core ──────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await db.Database.MigrateAsync();

    // Habilitar WAL mode para alto rendimiento
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

// ─── Middleware Pipeline ──────────────────────────────
app.UseStaticFiles();
app.UseAntiforgery();

// ─── Minimal API Endpoints ────────────────────────────
app.MapIngestEndpoints();
app.MapTenantApiEndpoints();

// ─── Blazor Server ────────────────────────────────────
app.MapRazorComponents<Dotar.Gateway.Dashboard.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Partial class para poder referenciarlo desde los tests
public partial class Program { }
