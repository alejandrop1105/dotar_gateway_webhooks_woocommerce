using Dotar.Gateway.Application;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios para Result y Result&lt;T&gt; (tipo envelope de la capa de aplicación).
/// Ejecutar con: dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj
/// </summary>
public class ResultTests
{
    // ─── Result (sin valor) ───────────────────────────────────────────────────

    [Fact]
    public void Result_Success_IsSuccessTrue_ErrorNone_MessageNull()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.Equal(ResultError.None, result.Error);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Result_Failure_WithValidation_IsSuccessFalse_ErrorValidation()
    {
        var result = Result.Failure(ResultError.Validation, "campo inválido");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Equal("campo inválido", result.Message);
    }

    [Fact]
    public void Result_Validation_Shortcut_IsSuccessFalse_ErrorValidation()
    {
        var result = Result.Validation("slug vacío");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Equal("slug vacío", result.Message);
    }

    [Fact]
    public void Result_NotFound_Shortcut_IsSuccessFalse_ErrorNotFound()
    {
        var result = Result.NotFound("tenant no encontrado");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
        Assert.Equal("tenant no encontrado", result.Message);
    }

    [Fact]
    public void Result_Conflict_Shortcut_IsSuccessFalse_ErrorConflict()
    {
        var result = Result.Conflict("slug duplicado");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Conflict, result.Error);
        Assert.Equal("slug duplicado", result.Message);
    }

    // ─── Result<T> (con valor) ────────────────────────────────────────────────

    [Fact]
    public void ResultT_Success_IsSuccessTrue_ErrorNone_ValueSet()
    {
        var result = Result<string>.Success("hola");

        Assert.True(result.IsSuccess);
        Assert.Equal(ResultError.None, result.Error);
        Assert.Null(result.Message);
        Assert.Equal("hola", result.Value);
    }

    [Fact]
    public void ResultT_Failure_IsSuccessFalse_ErrorSet_ValueDefault()
    {
        var result = Result<string>.Failure(ResultError.Conflict, "ya existe");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Conflict, result.Error);
        Assert.Equal("ya existe", result.Message);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ResultT_Validation_Shortcut_IsSuccessFalse_ErrorValidation()
    {
        var result = Result<int>.Validation("valor inválido");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Validation, result.Error);
        Assert.Equal("valor inválido", result.Message);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void ResultT_NotFound_Shortcut_IsSuccessFalse_ErrorNotFound()
    {
        var result = Result<object>.NotFound("recurso no encontrado");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.NotFound, result.Error);
        Assert.Equal("recurso no encontrado", result.Message);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ResultT_Conflict_Shortcut_IsSuccessFalse_ErrorConflict()
    {
        var result = Result<object>.Conflict("conflicto de unicidad");

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultError.Conflict, result.Error);
        Assert.Equal("conflicto de unicidad", result.Message);
        Assert.Null(result.Value);
    }

    // ─── ResultError enum values ──────────────────────────────────────────────

    [Fact]
    public void ResultError_None_ValueIsZero()
    {
        Assert.Equal(0, (int)ResultError.None);
    }

    [Fact]
    public void ResultError_Enum_ContainsExpectedMembers()
    {
        var values = Enum.GetValues<ResultError>();
        Assert.Contains(ResultError.None, values);
        Assert.Contains(ResultError.Validation, values);
        Assert.Contains(ResultError.NotFound, values);
        Assert.Contains(ResultError.Conflict, values);
    }
}
