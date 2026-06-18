using Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Contrato de caché de configuración de tenants.
/// Permite aislar TenantCacheService en tests unitarios mediante fake/mock.
/// </summary>
public interface ITenantCacheService
{
    /// <summary>Invalida la entrada de caché para el slug dado.</summary>
    void Invalidate(string slug);

    /// <summary>Obtiene un tenant por slug, usando caché con patrón cache-aside.</summary>
    Task<Tenant?> GetBySlugAsync(string slug);
}
