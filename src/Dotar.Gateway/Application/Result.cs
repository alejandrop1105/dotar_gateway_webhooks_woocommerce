namespace Dotar.Gateway.Application;

/// <summary>
/// Categoría de fallo de una operación de aplicación.
/// El caller mapea la categoría a su mecanismo de transporte:
/// API → status code; Blazor → severidad de alerta.
/// </summary>
public enum ResultError
{
    None = 0,
    Validation,   // entrada inválida → API 400 BadRequest
    NotFound,     // recurso inexistente → API 404 NotFound
    Conflict      // violación de unicidad → API 409 Conflict
}

/// <summary>
/// Resultado sin valor de retorno (delete, toggle, update void).
/// Ninguna operación lanza excepción por casos de negocio; siempre devuelve Result.
/// </summary>
public sealed record Result
{
    public bool IsSuccess { get; }
    public ResultError Error { get; }
    public string? Message { get; }

    private Result(bool isSuccess, ResultError error, string? message)
        => (IsSuccess, Error, Message) = (isSuccess, error, message);

    /// <summary>Operación completada exitosamente.</summary>
    public static Result Success() => new(true, ResultError.None, null);

    /// <summary>Operación fallida con categoría y mensaje descriptivo.</summary>
    public static Result Failure(ResultError error, string message) => new(false, error, message);

    // Atajos semánticos para legibilidad en el AppService.
    public static Result Validation(string message) => Failure(ResultError.Validation, message);
    public static Result NotFound(string message)   => Failure(ResultError.NotFound, message);
    public static Result Conflict(string message)   => Failure(ResultError.Conflict, message);
}

/// <summary>
/// Resultado con valor de retorno (create, get, update con entidad resultante).
/// El campo Value solo es significativo cuando IsSuccess es true.
/// </summary>
public sealed record Result<T>
{
    public bool IsSuccess { get; }
    public ResultError Error { get; }
    public string? Message { get; }
    public T? Value { get; }

    private Result(bool isSuccess, ResultError error, string? message, T? value)
        => (IsSuccess, Error, Message, Value) = (isSuccess, error, message, value);

    /// <summary>Operación completada exitosamente con el valor resultante.</summary>
    public static Result<T> Success(T value) => new(true, ResultError.None, null, value);

    /// <summary>Operación fallida. Value queda en default(T).</summary>
    public static Result<T> Failure(ResultError error, string message) => new(false, error, message, default);

    // Atajos semánticos para legibilidad en el AppService.
    public static Result<T> Validation(string message) => Failure(ResultError.Validation, message);
    public static Result<T> NotFound(string message)   => Failure(ResultError.NotFound, message);
    public static Result<T> Conflict(string message)   => Failure(ResultError.Conflict, message);
}
