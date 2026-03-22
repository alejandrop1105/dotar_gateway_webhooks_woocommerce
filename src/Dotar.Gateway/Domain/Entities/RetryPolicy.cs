using System.ComponentModel.DataAnnotations.Schema;

namespace Dotar.Gateway.Domain.Entities;

/// <summary>
/// Política de reintento nombrada con pasos configurables y circuit breaker.
/// Cada paso define un delay específico (desde segundos hasta días).
/// </summary>
public class RetryPolicy
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    /// <summary>Circuit Breaker: requests mínimos antes de evaluar ratio de fallas.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Circuit Breaker: duración con circuito abierto (en segundos).</summary>
    public int CircuitBreakerDurationSeconds { get; set; } = 30;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Pasos de reintento ordenados por StepNumber.</summary>
    public ICollection<RetryStep> Steps { get; set; } = [];
    public ICollection<Tenant> Tenants { get; set; } = [];

    /// <summary>Cantidad total de reintentos (= cantidad de pasos).</summary>
    [NotMapped]
    public int MaxRetryAttempts => Steps.Count;
}
