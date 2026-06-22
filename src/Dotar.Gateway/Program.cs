using Dotar.Gateway.Endpoints;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Dotar.Gateway.Infrastructure.Services;
using Dotar.Gateway.Providers;
using Dotar.Gateway.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using StackExchange.Redis;
using System.Threading.RateLimiting;

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
builder.Services.AddSingleton<ITenantCacheService, TenantCacheService>();
builder.Services.AddSingleton<TunnelStatusService>();
builder.Services.AddSingleton<Dotar.Gateway.Infrastructure.Tunnel.CloudflareTunnelManager>();
builder.Services.AddSingleton<MonitorNotificationService>();
builder.Services.AddSingleton<SystemLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemLogService>());
builder.Services.AddSingleton<DeployHistoryService>();
builder.Services.AddTransient<ForwardingService>();
builder.Services.AddScoped<Dotar.Gateway.Endpoints.ApiKeyEndpointFilter>();
builder.Services.AddScoped<Dotar.Gateway.Application.TenantAppService>();
builder.Services.AddScoped<Dotar.Gateway.Application.CajaRegistradaAppService>();
builder.Services.AddScoped<Dotar.Gateway.Application.ProveedorWebhookConfigAppService>();
builder.Services.AddSingleton<ICajaRegistradaCacheService, CajaRegistradaCacheService>();

// ─── HttpClientFactory para reenvío ───────────────────
builder.Services.AddHttpClient("GatewayForwarder", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "DotarGateway/1.0");
});

// ─── HttpClients dedicados para proveedores ───────────
// Enriquecimiento: timeout corto (10s) — API del proveedor debe ser rápida
builder.Services.AddHttpClient("ProviderEnrichment", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "DotarGateway/1.0");
});

// Callback a cajas: sin auto-redirect (seguridad anti-SSRF; la redirección debe ser explícita)
builder.Services.AddHttpClient("CajaCallback", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});

// ─── Keyed DI — Providers de webhook ────────────────
builder.Services.AddKeyedSingleton<IWebhookProvider, MercadoPagoProvider>(
    "mercadopago",
    (sp, _) =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("ProviderEnrichment");
        var logger = sp.GetRequiredService<ILogger<MercadoPagoProvider>>();
        return new MercadoPagoProvider(client, logger);
    });

// ─── Worker Background Services ──────────────────────
// Worker como singleton para que el Monitor pueda invocarlo para retry manual.
// IKeyedServiceProvider: el root IServiceProvider implementa esta interfaz en .NET 8+
builder.Services.AddSingleton<WebhookDispatcherWorker>(sp =>
    new WebhookDispatcherWorker(
        sp.GetRequiredService<RedisQueueService>(),
        sp.GetRequiredService<ForwardingService>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<MonitorNotificationService>(),
        sp.GetRequiredService<SystemLogService>(),
        sp.GetRequiredService<ILogger<WebhookDispatcherWorker>>(),
        (IKeyedServiceProvider)sp,
        sp.GetRequiredService<ICajaRegistradaCacheService>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcherWorker>());
builder.Services.AddHostedService<TunnelStartupService>();

// ─── Rate Limiting: registro de cajas — fixed window 10 req/min por IP ──────
builder.Services.AddRateLimiter(rl =>
{
    rl.AddFixedWindowLimiter(
        RegistroCajaEndpoints.RateLimiterPolicy,
        opts =>
        {
            opts.Window             = TimeSpan.FromMinutes(1);
            opts.PermitLimit        = 10;
            opts.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            opts.QueueLimit         = 0;
        });
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

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
app.UseRateLimiter();

// ─── Minimal API Endpoints ────────────────────────────
app.MapIngestEndpoints();
app.MapTenantApiEndpoints();
app.MapRegistroCajaEndpoints();
app.MapWebhookProveedorEndpoints();
app.MapProveedorConfigApiEndpoints();

// ─── Blazor Server ────────────────────────────────────
app.MapRazorComponents<Dotar.Gateway.Dashboard.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Partial class para poder referenciarlo desde los tests
public partial class Program { }
