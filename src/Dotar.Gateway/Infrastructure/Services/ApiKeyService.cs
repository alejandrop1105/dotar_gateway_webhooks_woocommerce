using System.Security.Cryptography;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Gestiona la API Key estática del Gateway, persistida en AppSettings.
/// Si no existe al arrancar, se genera y registra en log.
/// Comparación timing-safe en cada validación.
/// </summary>
public class ApiKeyService
{
    public const string SettingKey = "Gateway:ApiKey";
    public const string HeaderName = "X-Gateway-Api-Key";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApiKeyService> _logger;
    private string? _cachedKey;

    public ApiKeyService(IServiceScopeFactory scopeFactory, ILogger<ApiKeyService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Garantiza que exista una API Key. Si no, genera una y la persiste.
    /// Llamar al iniciar la aplicación.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.Value))
        {
            _cachedKey = existing.Value;
            _logger.LogInformation("API Key del Gateway cargada (longitud {Length}).", existing.Value.Length);
            return;
        }

        var generated = GenerateKey();
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = generated, UpdatedAt = DateTime.UtcNow });
        else
        {
            existing.Value = generated;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        _cachedKey = generated;

        _logger.LogWarning(
            "API Key del Gateway generada automáticamente: {ApiKey} — guardala de forma segura. " +
            "Podés rotarla desde el Dashboard → Configuración.",
            generated);
    }

    /// <summary>Compara la key recibida contra la configurada en formato timing-safe.</summary>
    public bool Validate(string? providedKey)
    {
        if (string.IsNullOrWhiteSpace(providedKey)) return false;
        var expected = _cachedKey ?? LoadFromDb();
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedKey);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    /// <summary>Regenera la API Key y la persiste.</summary>
    public async Task<string> RegenerateAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey);
        var newKey = GenerateKey();
        if (setting is null)
            db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = newKey, UpdatedAt = DateTime.UtcNow });
        else
        {
            setting.Value = newKey;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        _cachedKey = newKey;
        _logger.LogInformation("API Key del Gateway regenerada manualmente.");
        return newKey;
    }

    /// <summary>Devuelve la API Key actual (para mostrar en el dashboard).</summary>
    public string? GetCurrent() => _cachedKey ?? LoadFromDb();

    private string? LoadFromDb()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var setting = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == SettingKey);
        _cachedKey = setting?.Value;
        return _cachedKey;
    }

    private static string GenerateKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
