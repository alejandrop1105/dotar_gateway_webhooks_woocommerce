using Dotar.Gateway.Endpoints;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
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

// ─── Data Protection: persistir las keys junto a la DB (en el volumen /app/data)
//     para no invalidar las sesiones del dashboard en cada redeploy ───
var sqliteConnForKeys = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=gateway.db";
var dbFilePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConnForKeys).DataSource;
var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbFilePath));
var keysDir = Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? "." : dataDir, "dataprotection-keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("DotarGateway");

// ─── Redis (lazy connect — no crash si no está disponible) ───
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6380";
    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

// ─── JSON: aceptar y emitir enums como string en /api/* (ej. "WooCommerce") ─
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: true));
});

// ─── Servicios de Infraestructura ─────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<HmacSignatureValidator>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<RedisQueueService>();
builder.Services.AddSingleton<TenantCacheService>();
builder.Services.AddSingleton<TunnelStatusService>();
builder.Services.AddSingleton<MonitorNotificationService>();
builder.Services.AddSingleton<SystemLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemLogService>());
builder.Services.AddSingleton<DeployHistoryService>();
builder.Services.AddTransient<ForwardingService>();
builder.Services.AddScoped<Dotar.Gateway.Endpoints.ApiKeyEndpointFilter>();

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

// ─── Garantizar API Key del Gateway ───────────────────
await app.Services.GetRequiredService<ApiKeyService>().EnsureInitializedAsync();

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
