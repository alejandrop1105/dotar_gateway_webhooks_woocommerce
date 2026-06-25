using Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Infrastructure.Services;

/// <summary>
/// Estado de la resolución de una caja en el padrón.
/// Distingue los dos motivos por los que el worker no puede rutear, que antes
/// se confundían en un único "no encontrada".
/// </summary>
public enum ResolucionCaja
{
    /// <summary>La caja existe y su heartbeat está vigente: se puede rutear.</summary>
    Encontrada,

    /// <summary>No hay ninguna caja con ese identificador en el padrón.</summary>
    NoEncontrada,

    /// <summary>La caja existe pero su UltimaVez supera el TTL (heartbeat vencido).</summary>
    Vencida
}

/// <summary>
/// Resultado de resolver una caja: el estado y, cuando aplica, la entidad y su UltimaVez.
/// </summary>
/// <param name="Caja">La caja resuelta, o null si el estado no es <see cref="ResolucionCaja.Encontrada"/>.</param>
/// <param name="Estado">El motivo de la resolución.</param>
/// <param name="UltimaVez">Último heartbeat conocido (presente también cuando está vencida, para diagnóstico).</param>
public readonly record struct CajaResolucion(
    CajaRegistrada? Caja,
    ResolucionCaja Estado,
    DateTime? UltimaVez);

/// <summary>
/// Contrato del caché de cajas registradas (hot path del worker).
/// Singleton con patrón cache-aside: usa IMemoryCache + scope-per-miss para GatewayDbContext.
/// </summary>
public interface ICajaRegistradaCacheService
{
    /// <summary>
    /// Resuelve una caja por (TenantId, Identificador) usando cache-aside, distinguiendo
    /// si no existe (<see cref="ResolucionCaja.NoEncontrada"/>) o si existe pero su heartbeat
    /// venció (<see cref="ResolucionCaja.Vencida"/>). El worker usa el estado para emitir un
    /// mensaje de dead-letter preciso.
    /// </summary>
    Task<CajaResolucion> ResolverAsync(int tenantId, string identificador);

    /// <summary>
    /// Obtiene una caja por (TenantId, Identificador) usando cache-aside.
    /// Retorna null si no existe o si su UltimaVez supera el TTL configurado.
    /// Conveniencia sobre <see cref="ResolverAsync"/> para callers que no necesitan el motivo.
    /// </summary>
    Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador);

    /// <summary>
    /// Invalida la entrada de cache para la clave dada.
    /// Llamado desde CajaRegistradaAppService tras registrar o actualizar.
    /// </summary>
    void Invalidate(int tenantId, string identificador);
}
