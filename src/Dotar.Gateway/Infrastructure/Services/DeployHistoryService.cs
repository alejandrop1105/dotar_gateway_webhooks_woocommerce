using System.Text.Json;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Modelo de una entrada del historial de deploys (mapeado desde version.json).
/// </summary>
public record DeployEntry(
    string Version,
    DateTimeOffset BuiltAt,
    string DeployedBy,
    string Host,
    string BumpType,
    string GitCommit,
    string Changes,
    IReadOnlyList<string> Novedades
);

/// <summary>
/// Servicio que lee el historial de versiones desde version.json.
/// El archivo lo genera el script de deploy (deploy.ps1) y el Dockerfile lo copia
/// junto a los binarios publicados, de modo que vive al lado del ejecutable en producción.
/// </summary>
public class DeployHistoryService
{
    private readonly ILogger<DeployHistoryService> _logger;

    public DeployHistoryService(ILogger<DeployHistoryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Versión actualmente instalada (desde version.json).
    /// </summary>
    public string CurrentVersion { get; private set; } = "desconocida";

    /// <summary>
    /// Historial completo de deploys, del más reciente al más antiguo.
    /// </summary>
    public IReadOnlyList<DeployEntry> History { get; private set; } = [];

    /// <summary>
    /// Parsea el contenido de version.json y devuelve la versión actual y el historial de entradas.
    /// Extraído para testeo unitario sin dependencias de sistema de archivos.
    /// </summary>
    public static (string CurrentVersion, IReadOnlyList<DeployEntry> History) ParseVersionJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var currentVersion = root.TryGetProperty("current", out var cur)
            ? cur.GetString() ?? "desconocida"
            : "desconocida";

        var entries = new List<DeployEntry>();
        if (root.TryGetProperty("history", out var historyEl)
            && historyEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in historyEl.EnumerateArray())
            {
                var version    = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var builtAt    = item.TryGetProperty("builtAt", out var bat)
                    && DateTimeOffset.TryParse(bat.GetString(), out var parsed)
                        ? parsed
                        : DateTimeOffset.Now;
                var deployedBy = item.TryGetProperty("deployedBy", out var dby) ? dby.GetString() ?? "" : "";
                var host       = item.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                var bumpType   = item.TryGetProperty("bumpType", out var bt) ? bt.GetString() ?? "patch" : "patch";
                var gitCommit  = item.TryGetProperty("gitCommit", out var gc) ? gc.GetString() ?? "" : "";
                var changes    = item.TryGetProperty("changes", out var ch) ? ch.GetString() ?? "" : "";

                // Campo opcional: array de novedades en lenguaje de negocio.
                // Si no existe o no es array, queda lista vacía. Elementos no-string se ignoran.
                List<string> novedades = [];
                if (item.TryGetProperty("novedades", out var nov)
                    && nov.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in nov.EnumerateArray())
                    {
                        if (n.ValueKind == JsonValueKind.String)
                        {
                            var s = n.GetString();
                            if (!string.IsNullOrEmpty(s))
                                novedades.Add(s);
                        }
                    }
                }

                entries.Add(new DeployEntry(version, builtAt, deployedBy, host, bumpType, gitCommit, changes, novedades));
            }
        }

        return (currentVersion, entries);
    }

    /// <summary>
    /// Carga (o recarga) el archivo version.json desde el directorio de la aplicación.
    /// </summary>
    public void Load()
    {
        var paths = new[]
        {
            // Producción: mismo directorio que el ejecutable (copiado por el Dockerfile).
            Path.Combine(AppContext.BaseDirectory, "version.json"),
            // Desarrollo: raíz del repo. BaseDirectory = src/Dotar.Gateway/bin/Debug/net9.0/
            // -> 5 niveles arriba para llegar a la raíz del repositorio.
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "version.json"))
        };

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var json = File.ReadAllText(path);
                var (currentVersion, entries) = ParseVersionJson(json);
                CurrentVersion = currentVersion;
                History = entries;
                _logger.LogInformation("version.json cargado: {Version}, {Count} entradas", CurrentVersion, entries.Count);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error leyendo version.json en {Path}", path);
            }
        }

        _logger.LogWarning("No se encontró version.json. Historial de deploys vacío.");
    }
}
