using System.Diagnostics;
using System.Net;
using System.Text;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Servicio de reenvío HTTP que entrega los webhooks al destino final del tenant.
/// Replica headers de forma transparente y añade identificación del Gateway.
/// </summary>
public class ForwardingService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<ForwardingService> _logger;

    public ForwardingService(
        IHttpClientFactory clientFactory,
        ILogger<ForwardingService> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reenvía el payload JSON al destino final.
    /// Retorna el código de estado, duración y mensaje de error si aplica.
    /// </summary>
    public async Task<ForwardResult> ForwardAsync(
        string targetUrl, string payload, string tenantSlug)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var client = _clientFactory.CreateClient("GatewayForwarder");
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            // Header personalizado para identificar que el dato viene del Gateway
            content.Headers.Add("X-Dotar-Gateway-ID", tenantSlug);

            var response = await client.PostAsync(targetUrl, content);
            sw.Stop();

            _logger.LogInformation(
                "Webhook reenviado a {Url} → {StatusCode} en {Duration}ms",
                targetUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);

            return new ForwardResult
            {
                StatusCode = (int)response.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                IsSuccess = response.IsSuccessStatusCode,
                ErrorMessage = response.IsSuccessStatusCode
                    ? null
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Timeout al reenviar a {Url} ({Duration}ms)", targetUrl, sw.ElapsedMilliseconds);
            return new ForwardResult
            {
                StatusCode = (int)HttpStatusCode.GatewayTimeout,
                DurationMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Timeout al conectar con el destino",
                IsSuccess = false
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error al reenviar webhook a {Url}", targetUrl);
            return new ForwardResult
            {
                StatusCode = 0,
                DurationMs = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                IsSuccess = false
            };
        }
    }
}

public class ForwardResult
{
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSuccess { get; set; }
}
