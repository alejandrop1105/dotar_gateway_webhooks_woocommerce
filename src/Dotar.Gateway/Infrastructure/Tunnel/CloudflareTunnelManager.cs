using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace Dotar.Gateway.Infrastructure.Tunnel;

/// <summary>
/// Administra el ciclo de vida de un Cloudflare Named Tunnel.
/// Provisionamiento automático vía API REST.
/// Auto-descarga de cloudflared.exe si no está presente.
/// Adaptado de WebHooks.LegacyEngine (net48 → .NET 9).
/// </summary>
public class CloudflareTunnelManager : IDisposable
{
    private Process? _process;
    private readonly System.Timers.Timer _watchdogTimer;
    private readonly int _localPort;
    private readonly string _exePath;
    private readonly string _configPath;
    private readonly CloudflareConfig _config;
    private readonly ILogger<CloudflareTunnelManager> _logger;
    private bool _isInstalling;
    private bool _stopRequested;
    private bool _isConnected;
    private TunnelLocalConfig? _localConfig;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string ExeName => IsWindows ? "cloudflared.exe" : "cloudflared";

    private static string CloudflaredDownloadUrl => IsWindows 
        ? "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe"
        : "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64";
    private const string CloudflareApiBase = "https://api.cloudflare.com/client/v4";

    public event EventHandler<string>? OnUrlGenerated;
    public event EventHandler<string>? OnStatusChanged;

    public CloudflareTunnelManager(
        int localPort,
        CloudflareConfig config,
        ILogger<CloudflareTunnelManager> logger)
    {
        _localPort = localPort;
        _config = config;
        _logger = logger;
        _exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExeName);
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tunnel_config.json");

        _watchdogTimer = new System.Timers.Timer(5000);
        _watchdogTimer.Elapsed += WatchdogTimer_Elapsed;
    }

    /// <summary>
    /// Arranca el túnel completo: auto-descarga → provisionar → ejecutar.
    /// </summary>
    public async Task StartAsync()
    {
        _stopRequested = false;
        if (_isInstalling) return;

        if (!_config.IsValid)
        {
            NotifyStatus("Configuración de Cloudflare incompleta", "🔴");
            return;
        }

        if (!CloudflaredExists())
        {
            await DownloadBinaryAsync();
            if (!CloudflaredExists()) return;
        }

        if (File.Exists(_configPath))
        {
            _localConfig = LoadLocalConfig();
            if (_localConfig.Hostname != _config.Hostname)
            {
                _localConfig.Hostname = _config.Hostname;
                SaveLocalConfig(_localConfig);
            }
            NotifyStatus("Configuración local encontrada, conectando túnel existente...", "🟡");
            _logger.LogInformation("Reutilizando túnel existente: {Hostname}", _localConfig.Hostname);
        }
        else
        {
            NotifyStatus("Primera ejecución: provisionando túnel en Cloudflare...", "🟡");
            if (!await ProvisionTunnelAsync()) return;
        }

        OnUrlGenerated?.Invoke(this, $"https://{_config.Hostname}");
        StartTunnelProcess();
    }

    public void Stop()
    {
        _stopRequested = true;
        _isConnected = false;
        _watchdogTimer.Stop();
        KillProcessSafely();
        NotifyStatus("Detenido intencionalmente", "🔴");
    }

    // ─── Provisionamiento vía API Cloudflare ────────────

    private async Task<bool> ProvisionTunnelAsync()
    {
        try
        {
            using var http = CreateHttpClient();

            string? tunnelId = null;
            string? tunnelToken = null;

            var existing = await FindExistingTunnelAsync(http);
            if (existing is not null)
            {
                tunnelId = existing.Value.Id;
                NotifyStatus($"Túnel '{_config.TunnelName}' ya existe, reutilizando...", "🟡");
                tunnelToken = await GetTunnelTokenAsync(http, tunnelId);
            }
            else
            {
                NotifyStatus($"Creando túnel '{_config.TunnelName}'...", "🟡");
                var created = await CreateTunnelAsync(http);
                if (created is null) return false;
                tunnelId = created.Value.Id;
                tunnelToken = created.Value.Token;
            }

            await EnsureDnsRecordAsync(http, tunnelId);
            await ConfigureTunnelIngressAsync(http, tunnelId);

            _localConfig = new TunnelLocalConfig
            {
                TunnelName = _config.TunnelName,
                TunnelId = tunnelId,
                TunnelToken = tunnelToken!,
                Hostname = _config.Hostname
            };
            SaveLocalConfig(_localConfig);

            NotifyStatus("Provisionamiento exitoso", "🟢");
            return true;
        }
        catch (Exception ex)
        {
            NotifyStatus($"Error de provisionamiento: {ex.Message}", "🔴");
            _logger.LogError(ex, "Error durante provisionamiento de túnel Cloudflare");
            return false;
        }
    }

    private async Task<(string Id, string Token)?> FindExistingTunnelAsync(HttpClient http)
    {
        var url = $"{CloudflareApiBase}/accounts/{_config.AccountId}/cfd_tunnel?name={_config.TunnelName}&is_deleted=false";
        var response = await http.GetAsync(url);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        if (!response.IsSuccessStatusCode || json?["success"]?.GetValue<bool>() != true)
            return null;

        var tunnels = json?["result"]?.AsArray();
        if (tunnels is null || tunnels.Count == 0)
            return null;

        var id = tunnels[0]?["id"]?.GetValue<string>()!;
        var token = await GetTunnelTokenAsync(http, id);
        return (id, token);
    }

    private async Task<string> GetTunnelTokenAsync(HttpClient http, string tunnelId)
    {
        var url = $"{CloudflareApiBase}/accounts/{_config.AccountId}/cfd_tunnel/{tunnelId}/token";
        var response = await http.GetAsync(url);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        if (response.IsSuccessStatusCode && json?["success"]?.GetValue<bool>() == true)
            return json["result"]!.GetValue<string>();

        throw new InvalidOperationException($"No se pudo obtener token del túnel: {json?["errors"]}");
    }

    private async Task<(string Id, string Token)?> CreateTunnelAsync(HttpClient http)
    {
        var url = $"{CloudflareApiBase}/accounts/{_config.AccountId}/cfd_tunnel";
        var secretBytes = RandomNumberGenerator.GetBytes(32);

        var body = new
        {
            name = _config.TunnelName,
            tunnel_secret = Convert.ToBase64String(secretBytes),
            config_src = "cloudflare"
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await http.PostAsync(url, content);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        if (!response.IsSuccessStatusCode || json?["success"]?.GetValue<bool>() != true)
        {
            var errors = json?["errors"]?.ToJsonString() ?? "Error desconocido";
            NotifyStatus($"Error al crear túnel: {errors}", "🔴");
            _logger.LogError("API Cloudflare error al crear túnel: {Errors}", errors);
            return null;
        }

        var tunnelId = json["result"]!["id"]!.GetValue<string>();
        var token = await GetTunnelTokenAsync(http, tunnelId);
        await ConfigureTunnelIngressAsync(http, tunnelId);

        return (tunnelId, token);
    }

    private async Task ConfigureTunnelIngressAsync(HttpClient http, string tunnelId)
    {
        var url = $"{CloudflareApiBase}/accounts/{_config.AccountId}/cfd_tunnel/{tunnelId}/configurations";
        var configBody = new
        {
            config = new
            {
                ingress = new object[]
                {
                    new
                    {
                        hostname = _config.Hostname,
                        service = $"http://127.0.0.1:{_localPort}",
                        originRequest = new { httpHostHeader = "localhost" }
                    },
                    new { hostname = (string?)null, service = "http_status:404" }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(configBody), Encoding.UTF8, "application/json");
        var response = await http.PutAsync(url, content);

        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Advertencia al configurar ingress: {Error}", await response.Content.ReadAsStringAsync());
        else
            _logger.LogInformation("Ingress del túnel configurado vía API");
    }

    private async Task EnsureDnsRecordAsync(HttpClient http, string tunnelId)
    {
        var hostname = _config.Hostname;
        var checkUrl = $"{CloudflareApiBase}/zones/{_config.ZoneId}/dns_records?name={hostname}&type=CNAME";
        var checkResponse = await http.GetAsync(checkUrl);
        var checkJson = JsonNode.Parse(await checkResponse.Content.ReadAsStringAsync());

        if (checkResponse.IsSuccessStatusCode && checkJson?["success"]?.GetValue<bool>() == true)
        {
            var records = checkJson["result"]?.AsArray();
            if (records is not null && records.Count > 0)
            {
                _logger.LogInformation("Registro DNS ya existe: {Hostname}", hostname);
                return;
            }
        }

        NotifyStatus($"Creando registro DNS: {hostname}...", "🟡");
        var dnsBody = new
        {
            type = "CNAME",
            name = hostname,
            content = $"{tunnelId}.cfargotunnel.com",
            proxied = true
        };

        var content = new StringContent(JsonSerializer.Serialize(dnsBody), Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"{CloudflareApiBase}/zones/{_config.ZoneId}/dns_records", content);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        if (response.IsSuccessStatusCode && json?["success"]?.GetValue<bool>() == true)
            _logger.LogInformation("DNS CNAME creado: {Hostname}", hostname);
        else
            _logger.LogWarning("Advertencia DNS: {Errors}", json?["errors"]?.ToJsonString());
    }

    // ─── Ejecución del proceso cloudflared ───────────────

    private void StartTunnelProcess()
    {
        KillProcessSafely();
        _isConnected = false;

        NotifyStatus("Iniciando proceso cloudflared...", "🟡");

        try
        {
            _process = new Process();
            var finalPath = File.Exists(_exePath) ? _exePath : "cloudflared";
            _process.StartInfo.FileName = finalPath;
            _process.StartInfo.Arguments = $"tunnel --protocol http2 run --token {_localConfig!.TunnelToken}";
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.CreateNoWindow = true;
            _process.ErrorDataReceived += Process_ErrorDataReceived;
            _process.Start();
            _process.BeginErrorReadLine();
            _watchdogTimer.Start();
            NotifyStatus("Conectando a Cloudflare Edge...", "🟡");
        }
        catch (Exception ex)
        {
            NotifyStatus($"Error al iniciar cloudflared: {ex.Message}", "🔴");
        }
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        if (e.Data.Contains("ERR ") || e.Data.Contains("INF ") || e.Data.Contains("WRN "))
        {
            _logger.LogDebug("[Cloudflared] {Data}", e.Data);
            if (e.Data.Contains("ERR") || e.Data.Contains("404") || e.Data.Contains("502"))
                _logger.LogWarning("[Proxy Log] {Data}", e.Data);
        }

        if (e.Data.Contains("Registered tunnel connection") && !_isConnected)
        {
            _isConnected = true;
            NotifyStatus("Establecido y Público", "🟢");
        }
    }

    // ─── Auto-descarga de cloudflared.exe ─────────────────

    private bool CloudflaredExists()
    {
        if (File.Exists(_exePath)) return true;
        var separator = IsWindows ? ';' : ':';
        var systemPath = Environment.GetEnvironmentVariable("PATH");
        var paths = systemPath?.Split(separator) ?? [];
        return paths.Any(p => File.Exists(Path.Combine(p, ExeName)));
    }

    private async Task DownloadBinaryAsync()
    {
        _isInstalling = true;
        NotifyStatus($"Auto-descargando {ExeName} (~60MB)...", "🟠");

        try
        {
            using var hc = new HttpClient();
            var response = await hc.GetAsync(CloudflaredDownloadUrl);
            response.EnsureSuccessStatusCode();
            await using var fs = new FileStream(_exePath, FileMode.Create);
            await response.Content.CopyToAsync(fs);
            
            if (!IsWindows)
            {
#pragma warning disable CA1416
                File.SetUnixFileMode(_exePath, 
                    UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | 
                    UnixFileMode.GroupExecute | UnixFileMode.GroupRead | 
                    UnixFileMode.OtherExecute | UnixFileMode.OtherRead);
#pragma warning restore CA1416
            }

            NotifyStatus($"{ExeName} descargado exitosamente", "🟢");
        }
        catch (Exception ex)
        {
            NotifyStatus($"Error descargando cloudflared: {ex.Message}", "🔴");
            _logger.LogError(ex, "Error al descargar cloudflared.exe");
        }
        finally
        {
            _isInstalling = false;
        }
    }

    // ─── Watchdog y Proceso ────────────────────────────────

    private void WatchdogTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_stopRequested) return;
        if (_process is null || _process.HasExited)
        {
            _watchdogTimer.Stop();
            NotifyStatus("Detectada caída de red (Auto-Reconectando...)", "🟠");
            StartTunnelProcess();
        }
    }

    private void KillProcessSafely()
    {
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(); } catch { /* safe kill */ }
        }
        _process?.Dispose();
        _process = null;
    }

    // ─── Persistencia local ────────────────────────────────

    private TunnelLocalConfig LoadLocalConfig()
    {
        var json = File.ReadAllText(_configPath);
        return JsonSerializer.Deserialize<TunnelLocalConfig>(json)!;
    }

    private void SaveLocalConfig(TunnelLocalConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
        _logger.LogInformation("Config local guardada en: {Path}", _configPath);
    }

    private HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiToken);
        return client;
    }

    private void NotifyStatus(string message, string icon)
    {
        OnStatusChanged?.Invoke(this, $"{icon} {message}");
    }

    public void Dispose()
    {
        Stop();
        _watchdogTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal class TunnelLocalConfig
{
    public string TunnelName { get; set; } = string.Empty;
    public string TunnelId { get; set; } = string.Empty;
    public string TunnelToken { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
}
