using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dotar.Gateway.Tests.Application;

/// <summary>
/// Tests unitarios de CajaRegistradaCacheService.
/// Verifica comportamiento cache-aside: miss llama DB, hit retorna de memoria,
/// Invalidate limpia y fuerza próximo miss.
/// </summary>
public class ICajaRegistradaCacheService_Test : IDisposable
{
    private readonly GatewayDbContext _db;
    private readonly string _dbPath;
    private readonly IMemoryCache _memoryCache;
    private readonly CajaRegistradaCacheService _cacheService;
    private readonly Tenant _tenant;

    public ICajaRegistradaCacheService_Test()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new GatewayDbContext(options);
        _db.Database.EnsureCreated();

        _tenant = new Tenant
        {
            Name = "Cache Tenant",
            Slug = "cache-tenant",
            TargetUrl = "https://ejemplo.com",
            WebhookSecret = "secret",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        // Construir scope factory que resuelve el GatewayDbContext desde la misma DB
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seguridad:CajaTtlMinutos"] = "30"
            })
            .Build();

        _cacheService = new CajaRegistradaCacheService(
            _memoryCache, scopeFactory, config,
            NullLogger<CajaRegistradaCacheService>.Instance);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task GetByIdentificadorAsync_Miss_ConsultaDB()
    {
        // Insertar caja directamente en DB (sin pasar por cache)
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-MISS",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _cacheService.GetByIdentificadorAsync(_tenant.Id, "CAJA-MISS");

        Assert.NotNull(result);
        Assert.Equal("CAJA-MISS", result!.Identificador);
    }

    [Fact]
    public async Task GetByIdentificadorAsync_Hit_RetornaDesdeCache()
    {
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-HIT",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Primera llamada: miss → carga en cache
        var first = await _cacheService.GetByIdentificadorAsync(_tenant.Id, "CAJA-HIT");
        Assert.NotNull(first);

        // Modificar en DB directamente (sin invalidar cache)
        var entidad = await _db.CajasRegistradas
            .FirstAsync(c => c.Identificador == "CAJA-HIT");
        entidad.CallbackUrl = "https://otro.cfargotunnel.com/cb";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Segunda llamada: debe venir del cache (URL original)
        var second = await _cacheService.GetByIdentificadorAsync(_tenant.Id, "CAJA-HIT");
        Assert.Equal("https://tunel.cfargotunnel.com/cb", second!.CallbackUrl);
    }

    [Fact]
    public async Task Invalidate_LimpiaCache_FuerzaProximaMissEnDB()
    {
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-INV",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Cargar en cache
        await _cacheService.GetByIdentificadorAsync(_tenant.Id, "CAJA-INV");

        // Actualizar en DB y luego invalidar
        var entidad = await _db.CajasRegistradas
            .FirstAsync(c => c.Identificador == "CAJA-INV");
        entidad.CallbackUrl = "https://nuevo.dotarsoluciones.com/cb";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _cacheService.Invalidate(_tenant.Id, "CAJA-INV");

        // Ahora debe ir a la DB y obtener la URL nueva
        var result = await _cacheService.GetByIdentificadorAsync(_tenant.Id, "CAJA-INV");
        Assert.Equal("https://nuevo.dotarsoluciones.com/cb", result!.CallbackUrl);
    }

    [Fact]
    public async Task GetByIdentificadorAsync_CajaInexistente_RetornaNull()
    {
        var result = await _cacheService.GetByIdentificadorAsync(_tenant.Id, "NO-EXISTE");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolverAsync_CajaInexistente_RetornaNoEncontrada()
    {
        var resolucion = await _cacheService.ResolverAsync(_tenant.Id, "NO-EXISTE");

        Assert.Equal(ResolucionCaja.NoEncontrada, resolucion.Estado);
        Assert.Null(resolucion.Caja);
        Assert.Null(resolucion.UltimaVez);
    }

    [Fact]
    public async Task ResolverAsync_CajaConHeartbeatVencido_RetornaVencidaConUltimaVez()
    {
        var ultimaVez = DateTime.UtcNow.AddHours(-2); // > 30 min de TTL
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-VENCIDA",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = ultimaVez
        });
        await _db.SaveChangesAsync();

        var resolucion = await _cacheService.ResolverAsync(_tenant.Id, "CAJA-VENCIDA");

        Assert.Equal(ResolucionCaja.Vencida, resolucion.Estado);
        Assert.Null(resolucion.Caja);
        Assert.NotNull(resolucion.UltimaVez);
    }

    [Fact]
    public async Task ResolverAsync_CajaVigente_RetornaEncontrada()
    {
        _db.CajasRegistradas.Add(new CajaRegistrada
        {
            TenantId = _tenant.Id,
            Identificador = "CAJA-VIGENTE",
            CallbackUrl = "https://tunel.cfargotunnel.com/cb",
            UltimaVez = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var resolucion = await _cacheService.ResolverAsync(_tenant.Id, "CAJA-VIGENTE");

        Assert.Equal(ResolucionCaja.Encontrada, resolucion.Estado);
        Assert.NotNull(resolucion.Caja);
        Assert.Equal("CAJA-VIGENTE", resolucion.Caja!.Identificador);
    }
}
