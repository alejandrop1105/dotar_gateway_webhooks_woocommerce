using Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Contrato del caché de cajas registradas (hot path del worker).
/// Singleton con patrón cache-aside: usa IMemoryCache + scope-per-miss para GatewayDbContext.
/// </summary>
public interface ICajaRegistradaCacheService
{
    /// <summary>
    /// Obtiene una caja por (TenantId, Identificador) usando cache-aside.
    /// Retorna null si no existe o si su UltimaVez supera el TTL configurado.
    /// </summary>
    Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador);

    /// <summary>
    /// Invalida la entrada de cache para la clave dada.
    /// Llamado desde CajaRegistradaAppService tras registrar o actualizar.
    /// </summary>
    void Invalidate(int tenantId, string identificador);
}
