using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Services;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests de contrato de interfaz ITenantCacheService.
/// Verifica que la interfaz existe, es mockeable con un fake manual,
/// y que los métodos requeridos son invocables.
/// </summary>
public class TenantAppServiceCacheTests
{
    /// <summary>
    /// Fake manual de ITenantCacheService para tests unitarios del AppService.
    /// Registra las invocaciones para permitir assertions.
    /// </summary>
    private class FakeTenantCacheService : ITenantCacheService
    {
        public List<string> InvalidatedSlugs { get; } = [];
        public Dictionary<string, Tenant?> StoredTenants { get; } = [];

        public void Invalidate(string slug)
        {
            InvalidatedSlugs.Add(slug);
            StoredTenants.Remove(slug);
        }

        public Task<Tenant?> GetBySlugAsync(string slug)
        {
            StoredTenants.TryGetValue(slug, out var tenant);
            return Task.FromResult(tenant);
        }
    }

    [Fact]
    public void ITenantCacheService_Exists_And_IsInvocable()
    {
        // Arrange: crear un fake que implemente la interfaz
        ITenantCacheService cache = new FakeTenantCacheService();

        // Act & Assert: Invalidate es invocable
        cache.Invalidate("mi-tenant");
        // Si llega aquí sin excepción, la interfaz existe y el método es invocable.
    }

    [Fact]
    public void Invalidate_TracksCalls()
    {
        // Arrange
        var fake = new FakeTenantCacheService();

        // Act
        fake.Invalidate("slug-a");
        fake.Invalidate("slug-b");

        // Assert: el fake registra las invocaciones
        Assert.Contains("slug-a", fake.InvalidatedSlugs);
        Assert.Contains("slug-b", fake.InvalidatedSlugs);
        Assert.Equal(2, fake.InvalidatedSlugs.Count);
    }

    [Fact]
    public async Task GetBySlugAsync_IsInvocable()
    {
        // Arrange
        ITenantCacheService cache = new FakeTenantCacheService();

        // Act
        var result = await cache.GetBySlugAsync("no-existe");

        // Assert: devuelve null para slug no configurado
        Assert.Null(result);
    }

    [Fact]
    public void FakeCacheService_CanBeAssignedToInterface()
    {
        // Verifica que el fake cumple el contrato de la interfaz
        var fake = new FakeTenantCacheService();
        ITenantCacheService interfaceRef = fake;
        Assert.NotNull(interfaceRef);
    }
}
