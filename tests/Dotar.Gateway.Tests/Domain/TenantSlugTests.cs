using Dotar.Gateway.Domain.Entities;

namespace Dotar.Gateway.Tests.Domain;

/// <summary>
/// Tests unitarios para la API estática de slug en la entidad Tenant.
/// Verifica NormalizeSlug y IsValidSlug con el patrón canónico del proyecto.
/// </summary>
public class TenantSlugTests
{
    // ─── NormalizeSlug ────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeSlug_TrimsAndLowercases()
    {
        var result = Tenant.NormalizeSlug(" Mi-Tenant ");
        Assert.Equal("mi-tenant", result);
    }

    [Fact]
    public void NormalizeSlug_AlreadyNormalized_ReturnsSame()
    {
        var result = Tenant.NormalizeSlug("mi-tenant");
        Assert.Equal("mi-tenant", result);
    }

    [Fact]
    public void NormalizeSlug_UpperCase_Lowercases()
    {
        var result = Tenant.NormalizeSlug("UPPER");
        Assert.Equal("upper", result);
    }

    [Fact]
    public void NormalizeSlug_WithLeadingTrailingSpaces_Trims()
    {
        var result = Tenant.NormalizeSlug("  slug123  ");
        Assert.Equal("slug123", result);
    }

    // ─── IsValidSlug ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mi-tenant",   true)]
    [InlineData("a",           true)]  // slug de un carácter
    [InlineData("abc123",      true)]
    [InlineData("a1",          true)]
    [InlineData("a-b",         true)]
    public void IsValidSlug_ValidSlugs_ReturnsTrue(string slug, bool expected)
    {
        Assert.Equal(expected, Tenant.IsValidSlug(slug));
    }

    [Theory]
    [InlineData("with space",   false)]
    [InlineData("UPPER",        false)]  // debe ser lowercase ya normalizado
    [InlineData("",             false)]
    [InlineData("-leading",     false)]
    [InlineData("trailing-",    false)]
    [InlineData("with/slash",   false)]
    public void IsValidSlug_InvalidSlugs_ReturnsFalse(string slug, bool expected)
    {
        Assert.Equal(expected, Tenant.IsValidSlug(slug));
    }

    [Fact]
    public void IsValidSlug_AfterNormalize_WithSpaces_ReturnsFalse()
    {
        // El slug "with space" normalizado sigue siendo inválido (tiene espacio)
        var normalized = Tenant.NormalizeSlug("with space");
        Assert.False(Tenant.IsValidSlug(normalized));
    }

    [Fact]
    public void IsValidSlug_AfterNormalize_UpperCase_ReturnsTrueIfOtherwiseValid()
    {
        // "UPPER" normalizado → "upper" → válido
        var normalized = Tenant.NormalizeSlug("UPPER");
        Assert.True(Tenant.IsValidSlug(normalized));
    }

    [Fact]
    public void IsValidSlug_AfterNormalize_WithSpacesAndUpper_Workflow()
    {
        // Workflow completo: normalizar primero, luego validar
        var raw = " Mi-Tenant ";
        var normalized = Tenant.NormalizeSlug(raw);
        Assert.Equal("mi-tenant", normalized);
        Assert.True(Tenant.IsValidSlug(normalized));
    }
}
