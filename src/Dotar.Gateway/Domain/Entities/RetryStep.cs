namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Unidad de tiempo para los delays de reintento.
/// </summary>
public enum DelayUnit
{
    Seconds,
    Minutes,
    Hours,
    Days
}

/// <summary>
/// Paso individual dentro de una política de reintento.
/// Cada paso define cuánto esperar antes de reintentar.
/// Ejemplo: Paso 1 = 5s, Paso 2 = 30s, Paso 3 = 5min, Paso 4 = 1h, Paso 5 = 24h
/// </summary>
public class RetryStep
{
    public int Id { get; set; }
    public int RetryPolicyId { get; set; }
    public int StepNumber { get; set; }
    public int DelayValue { get; set; }
    public DelayUnit DelayUnit { get; set; } = DelayUnit.Seconds;

    public RetryPolicy RetryPolicy { get; set; } = null!;

    /// <summary>Calcula el TimeSpan de este paso.</summary>
    public TimeSpan GetDelay() => DelayUnit switch
    {
        DelayUnit.Seconds => TimeSpan.FromSeconds(DelayValue),
        DelayUnit.Minutes => TimeSpan.FromMinutes(DelayValue),
        DelayUnit.Hours => TimeSpan.FromHours(DelayValue),
        DelayUnit.Days => TimeSpan.FromDays(DelayValue),
        _ => TimeSpan.FromSeconds(DelayValue)
    };

    /// <summary>Descripción legible del paso.</summary>
    public string DisplayText => DelayUnit switch
    {
        DelayUnit.Seconds => $"{DelayValue}s",
        DelayUnit.Minutes => $"{DelayValue}min",
        DelayUnit.Hours => $"{DelayValue}h",
        DelayUnit.Days => $"{DelayValue}d",
        _ => $"{DelayValue}s"
    };
}
