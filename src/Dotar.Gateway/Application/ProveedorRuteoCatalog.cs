namespace Dotar.Gateway.Application;

/// <summary>
/// Implementación del catálogo de proveedores de ruteo.
/// Recibe la lista de keys como constructor explícito (IEnumerable&lt;string&gt;)
/// para evitar el problema de keyed singletons no resueltos por IEnumerable en .NET 9.
/// Se registra en DI con una factory que pasa las keys conocidas explícitamente.
/// </summary>
public sealed class ProveedorRuteoCatalog : IProveedorRuteoCatalog
{
    private readonly IReadOnlyCollection<string> _keys;

    /// <param name="keys">Lista explícita de keys de proveedor válidas.</param>
    public ProveedorRuteoCatalog(IEnumerable<string> keys)
    {
        _keys = keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> KeysValidas => _keys;
}
