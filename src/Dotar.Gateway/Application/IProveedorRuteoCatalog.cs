namespace Dotar.Gateway.Application;

/// <summary>
/// Abstracción del catálogo de proveedores de ruteo registrados en el Gateway.
/// Permite validar que el nombre de proveedor elegido por un tenant es una key válida
/// sin acoplar la capa de aplicación al contenedor DI (keyed singletons).
/// </summary>
public interface IProveedorRuteoCatalog
{
    /// <summary>
    /// Colección de keys de proveedor disponibles (p.ej. "mercadopago", "woocommerce-multisucursal").
    /// </summary>
    IReadOnlyCollection<string> KeysValidas { get; }
}
