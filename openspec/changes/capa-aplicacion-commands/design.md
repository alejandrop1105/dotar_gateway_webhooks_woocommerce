# Diseño técnico: Capa de Aplicación para operaciones de Tenant

> Change: `capa-aplicacion-commands`
> Fase: design (arquitectura + enfoque de implementación). No incluye specs ni tareas.
> Alcance: solo CRUD de Tenant (crear, editar, borrar, toggle activo, actualizar target-url).

## 1. Resumen del enfoque

Se introduce una capa de Aplicación delgada bajo `src/Dotar.Gateway/Application/`, materializada en un único
**Application Service plano**, `TenantAppService`, registrado como **Scoped**. Concentra toda la lógica de negocio
de tenants (validación de slug, normalización, unicidad, validación de FKs, generación de secret, invalidación de
caché) que hoy está duplicada y divergente entre `Endpoints/TenantApiEndpoints.cs` y
`Dashboard/Components/Pages/Tenants.razor`.

Tanto los endpoints Minimal API como el componente Blazor pasan a **delegar** en `TenantAppService` y a traducir un
tipo de retorno `Result` / `Result<T>` propio a su mecanismo nativo (API → `Results.BadRequest/Conflict/NotFound`;
Blazor → `_errorMessage` + `MudAlert` / `Snackbar`). No se usa MediatR ni el patrón command/handler. No se modifica
el contrato HTTP de los endpoints existentes.

Decisión arquitectónica ya cerrada (no se re-debate): Application Services planos, `TenantAppService` con
`GatewayDbContext` por DI directa, `Result<T>` propio, slug inmutable tras creación, `SlugRegex` unificado. Lo que
este documento RESUELVE son las seis decisiones de diseño abiertas más abajo.

## 2. Verificación del estado real del código (evidencia, no suposición)

Lifetimes confirmados en `Program.cs`:

| Tipo | Registro | Lifetime | Evidencia |
|---|---|---|---|
| `GatewayDbContext` | `AddDbContext<GatewayDbContext>` | **Scoped** (default de `AddDbContext`) | `Program.cs:18` |
| `TenantCacheService` | `AddSingleton<TenantCacheService>` | **Singleton** | `Program.cs:60` |
| `ApiKeyService` | `AddSingleton<ApiKeyService>` | **Singleton** | `Program.cs:58` |
| `ApiKeyEndpointFilter` | `AddScoped<...>` | Scoped | `Program.cs:68` |

Consumidores actuales de la lógica de tenant:

- **Endpoints Minimal API** (`TenantApiEndpoints.cs`): los handlers resuelven dependencias por parámetro en cada
  request. El request HTTP corre dentro de un **scope** propio de ASP.NET Core; un handler puede recibir servicios
  Scoped directamente. Hoy usan `IServiceScopeFactory` (`TenantApiEndpoints.cs:66,89-90`) porque las dependencias
  que inyectan (`TenantCacheService`) son Singleton, pero NADA impide inyectar un Scoped en un handler de endpoint.
- **Componente Blazor** (`Tenants.razor`): los componentes de Blazor Server viven dentro del scope del circuito SignalR,
  que es **Scoped**. Hoy usa `@inject IServiceScopeFactory ScopeFactory` (`Tenants.razor:8`) por la misma razón
  histórica (las dependencias Singleton lo arrastraron a resolver el DbContext manualmente). Un componente Blazor
  Server PUEDE inyectar un servicio Scoped directamente.

Conclusión: ambos consumidores son compatibles con un `TenantAppService` **Scoped**. Ninguno es Singleton.

## 3. Las seis decisiones de diseño

### Decisión 1 — Lifetime: `TenantAppService` es **Scoped**

`TenantAppService` se registra como **Scoped** porque necesita `GatewayDbContext` (Scoped) por DI directa. Esto
elimina el patrón `IServiceScopeFactory.CreateScope()` que hoy se repite en cada handler/método.

**Regla de inyección entre lifetimes** (esto es lo que el riesgo del proposal pedía documentar):

- Un **Singleton NO puede** recibir un **Scoped** por constructor (capturaría un DbContext de un scope ya cerrado →
  `ObjectDisposedException` / captive dependency). Por eso `TenantCacheService` NO inyecta el DbContext: lo resuelve
  on-demand con `IServiceScopeFactory` (`TenantCacheService.cs:15,52`).
- Un **Scoped SÍ puede** recibir un **Singleton**. Esta es la dirección que usamos: `TenantAppService` (Scoped)
  inyecta `TenantCacheService` (Singleton) por constructor sin violar ninguna regla. La invalidación de caché
  (`tenantCache.Invalidate(slug)`) se invoca directamente.

```
TenantAppService (Scoped)
  ├── GatewayDbContext      (Scoped)    ← Scoped → Scoped: OK
  ├── TenantCacheService    (Singleton) ← Scoped → Singleton: OK (dirección válida)
  └── ILogger<TenantAppService>         ← cualquier lifetime: OK
```

Restricción a respetar a futuro: **ningún Singleton debe inyectar `TenantAppService`**. Los únicos consumidores
permitidos son los endpoints (scope por request) y los componentes Blazor (scope por circuito). Esto se documenta
aquí como invariante de la capa.

### Decisión 2 — Dónde vive `SlugRegex` y la normalización

`SlugRegex` y la normalización (`Trim().ToLowerInvariant()`) viven en la **entidad de dominio `Tenant`**, como API
estática del dominio. Justificación:

- El formato del slug es una **regla de dominio** (invariante de la entidad), no un detalle de transporte. Hoy está
  mal ubicada en la capa de entrega (`TenantApiEndpoints.cs:16`), por eso el dashboard pudo divergir.
- Ponerla en el dominio garantiza una **única fuente de verdad** accesible tanto desde `TenantAppService` como desde
  cualquier futuro consumidor, sin acoplar la capa de aplicación a un helper suelto ni a la capa de endpoints.
- Mantiene el dominio anémico-pero-honesto: la regla de qué es un slug válido pertenece a `Tenant`.

Firma propuesta (a agregar a `Domain/Entities/Tenant.cs`):

```csharp
public partial class Tenant
{
    // Regex idéntico al actual de TenantApiEndpoints.cs:16 (NO cambiar el patrón: los tests dependen de él).
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$|^[a-z0-9]$")]
    private static partial Regex SlugRegex();

    /// <summary>Normaliza un slug crudo a su forma canónica: trim + lowercase invariante.</summary>
    public static string NormalizeSlug(string raw) => raw.Trim().ToLowerInvariant();

    /// <summary>Valida que un slug YA normalizado cumpla el formato canónico.</summary>
    public static bool IsValidSlug(string normalizedSlug) => SlugRegex().IsMatch(normalizedSlug);
}
```

Nota de implementación: si convertir `Tenant` a `partial` con `[GeneratedRegex]` genera fricción (la clase hoy no es
`partial`), la alternativa equivalente es un campo `static readonly Regex` con `RegexOptions.Compiled` idéntico al de
`TenantApiEndpoints.cs:16`. Ambas son aceptables; lo NO negociable es que el patrón sea idéntico al actual y resida en
`Tenant`. El orden de uso siempre es: `var slug = Tenant.NormalizeSlug(input); if (!Tenant.IsValidSlug(slug)) ...`.

### Decisión 3 — Firma de `Result` y `Result<T>` (la más importante)

El `Result` lleva una **categoría de error** (`ResultError`) además del mensaje, para que cada caller mapee al
mecanismo correcto sin reparsear strings. La API necesita distinguir 400 / 404 / 409; el caller elige el status code
a partir de la categoría, no del texto.

```csharp
namespace Dotar.Gateway.Application;

/// <summary>Categoría de fallo de una operación de aplicación. El caller la mapea a su transporte
/// (API → status code; Blazor → severidad de alerta).</summary>
public enum ResultError
{
    None = 0,
    Validation,   // entrada inválida → API 400 BadRequest
    NotFound,     // recurso inexistente → API 404 NotFound
    Conflict      // violación de unicidad → API 409 Conflict
}

/// <summary>Resultado sin valor de retorno (delete, toggle, update void).</summary>
public sealed record Result
{
    public bool IsSuccess { get; }
    public ResultError Error { get; }
    public string? Message { get; }

    private Result(bool isSuccess, ResultError error, string? message)
        => (IsSuccess, Error, Message) = (isSuccess, error, message);

    public static Result Success() => new(true, ResultError.None, null);
    public static Result Failure(ResultError error, string message) => new(false, error, message);

    // Atajos semánticos para legibilidad en el AppService.
    public static Result Validation(string message) => Failure(ResultError.Validation, message);
    public static Result NotFound(string message)   => Failure(ResultError.NotFound, message);
    public static Result Conflict(string message)   => Failure(ResultError.Conflict, message);
}

/// <summary>Resultado con valor de retorno (create, get).</summary>
public sealed record Result<T>
{
    public bool IsSuccess { get; }
    public ResultError Error { get; }
    public string? Message { get; }
    public T? Value { get; }

    private Result(bool isSuccess, ResultError error, string? message, T? value)
        => (IsSuccess, Error, Message, Value) = (isSuccess, error, message, value);

    public static Result<T> Success(T value) => new(true, ResultError.None, null, value);
    public static Result<T> Failure(ResultError error, string message) => new(false, error, message, default);

    public static Result<T> Validation(string message) => Failure(ResultError.Validation, message);
    public static Result<T> NotFound(string message)   => Failure(ResultError.NotFound, message);
    public static Result<T> Conflict(string message)   => Failure(ResultError.Conflict, message);
}
```

Justificación de llevar `ResultError` (categoría) y no solo `string`:

- Hoy los endpoints devuelven explícitamente `BadRequest`, `Conflict`, `NotFound` (`TenantApiEndpoints.cs:71,93,165`).
  Si `Result` solo trajera un string, el endpoint tendría que inferir el status code del texto del mensaje — frágil y
  acopla el mensaje al transporte. La categoría desacopla: el AppService decide "esto es un conflicto", el endpoint
  decide "conflicto → 409".
- Blazor no usa status codes pero sí puede mapear la categoría a severidad de `Snackbar`/`MudAlert` si interesa
  (hoy todo es un `_errorMessage` plano; con la categoría podría diferenciar warning vs error sin reparsear).
- Mantiene el `Result` mínimo: tres propiedades + enum, sin librerías externas, alineado con el proposal
  (`IsSuccess`, `Error`, `Value`). Se renombra `Error` del proposal a `Message` (string) y se agrega `Error`
  (categoría enum). Es una mejora dirigida del contrato que el proposal dejaba abierta como "string o código+mensaje".

Mapeo de referencia que el endpoint usará (helper de extensión, capa de entrega, no de aplicación):

| `ResultError` | Status HTTP | Método `Results.*` |
|---|---|---|
| `Validation` | 400 | `BadRequest(new { error = msg })` |
| `NotFound` | 404 | `NotFound(new { error = msg })` |
| `Conflict` | 409 | `Conflict(new { error = msg })` |
| `None` (success) | 200/201/204 | según operación |

### Decisión 4 — Firmas de los métodos de `TenantAppService`

Entrada por **DTOs/records de input propios de la capa de aplicación** (no parámetros sueltos, no los `*Request` de
los endpoints). Razón: los `CreateTenantRequest`/`UpdateTenantRequest` viven en `TenantApiEndpoints.cs` (capa de
entrega) y arrastran semántica de transporte; la capa de aplicación no debe depender de ellos. Se definen inputs
propios en `Application/`, y el endpoint/Blazor los construye. Estos inputs reflejan exactamente los datos que hoy
manejan `CreateTenantRequest` (`TenantApiEndpoints.cs:349`), `UpdateTenantRequest` (`:364`) y `TenantFormModel`
(`Tenants.razor:363`).

```csharp
namespace Dotar.Gateway.Application;

/// <summary>Datos para crear un tenant. Equivale a CreateTenantRequest pero sin acoplar a la capa HTTP.</summary>
public sealed record CreateTenantInput(
    string Name,
    string Slug,
    string TargetUrl,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null);

/// <summary>Datos para actualización parcial. Campos null = no se modifican.
/// El slug NO está presente: es inmutable tras la creación (decisión del proposal).</summary>
public sealed record UpdateTenantInput(
    string? Name = null,
    string? TargetUrl = null,
    string? WebhookSecret = null,
    SignatureScheme? SignatureScheme = null,
    string? SignatureHeader = null,
    bool? IsActive = null,
    int? RetryPolicyId = null,
    int? TenantGroupId = null);
```

Servicio:

```csharp
namespace Dotar.Gateway.Application;

public sealed class TenantAppService
{
    private readonly GatewayDbContext _db;
    private readonly TenantCacheService _cache;
    private readonly ILogger<TenantAppService> _logger;

    public TenantAppService(
        GatewayDbContext db,
        TenantCacheService cache,
        ILogger<TenantAppService> logger)
        => (_db, _cache, _logger) = (db, cache, logger);

    /// <summary>Crea un tenant. Valida nombre/slug/url, formato de slug, unicidad y FKs.
    /// Genera secret base64 si corresponde. Invalida caché. Devuelve el Tenant creado.</summary>
    public Task<Result<Tenant>> CreateAsync(CreateTenantInput input);

    /// <summary>Actualización parcial por slug. Slug inmutable (no se cambia). 404 si no existe,
    /// 400 si url/FK inválida. Invalida la caché del slug. Devuelve el Tenant actualizado.</summary>
    public Task<Result<Tenant>> UpdateAsync(string slug, UpdateTenantInput input);

    /// <summary>Actualiza solo la target-url (operación dedicada del endpoint PUT /{slug}/target-url).</summary>
    public Task<Result<Tenant>> UpdateTargetUrlAsync(string slug, string targetUrl);

    /// <summary>Borra el tenant por slug (cascada de DeliveryLogs vía DbContext). 404 si no existe.</summary>
    public Task<Result> DeleteAsync(string slug);

    /// <summary>Invierte IsActive del tenant. 404 si no existe. Devuelve el nuevo estado.</summary>
    public Task<Result<Tenant>> ToggleActiveAsync(string slug);
}
```

Notas sobre las firmas:

- **Retorno `Result<Tenant>`** (la entidad) en create/update/toggle: el endpoint construye su payload anónimo
  (`TenantApiEndpoints.cs:131-143,225-237`) a partir del `Tenant`, y Blazor recarga su grilla. No se inventa un DTO de
  salida porque ambos consumidores ya proyectan desde la entidad. `DeleteAsync` devuelve `Result` (sin valor).
- **`UpdateAsync` recibe `slug` separado del input** porque el slug identifica el recurso (viene de la ruta
  `/{slug}` en la API y del `Id`→slug en Blazor) y NO es parte de los campos editables. Internamente normaliza el slug
  recibido con `Tenant.NormalizeSlug` antes de buscar, igual que `TenantApiEndpoints.cs:162`.
- **Toggle por slug** (no por `Id`): unifica con el resto de operaciones que trabajan por slug. Blazor hoy lo hace por
  `Id` (`Tenants.razor:354`); al delegar, Blazor pasa `tenant.Slug`. Esto NO cambia comportamiento observable.
- **`CreateAsync` valida FKs** (`RetryPolicyId`, `TenantGroupId`) replicando `TenantApiEndpoints.cs:95-99`, cerrando
  otra divergencia (Blazor hoy no valida que la FK exista, solo mapea `0 → null`).

### Decisión 5 — Generación de secret

La generación de secret base64 (32 bytes) hoy está triplicada: `TenantApiEndpoints.GenerateSecret()`
(`:337`), `Tenants.razor GenerateSecret()` (`:342`) y `ApiKeyService.GenerateKey()` (`:108`).

Decisión: la generación que pertenece al **secret de webhook de un tenant** se concentra dentro de
`TenantAppService` como helper privado `GenerateWebhookSecret()`, porque es una regla de negocio del tenant (cuándo se
autogenera: secret provisto → se usa; esquema `None` → vacío; resto → autogenerado, lógica de
`TenantApiEndpoints.cs:103-109`). El endpoint y Blazor dejan de generar secret por su cuenta al crear.

```csharp
private static string GenerateWebhookSecret()
{
    Span<byte> bytes = stackalloc byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToBase64String(bytes); // base64 por convención del proyecto (CLAUDE.md)
}
```

Caso del botón "Generar" del formulario Blazor (`Tenants.razor:342-348`): ese botón es **UI pura** (rellena el campo
del formulario antes de enviar, con preview `_showSecret`). NO es lógica de negocio de creación; puede permanecer en el
`.razor` como utilidad de UI. No se fuerza su migración en este change para no inflar el alcance ni tocar la UX del
formulario. Queda registrado como deuda menor: a futuro podría exponerse un `TenantAppService` helper público o un
util compartido. `ApiKeyService.GenerateKey()` queda fuera de alcance (es secret del Gateway, no del tenant).

### Decisión 6 — Estructura de carpetas y namespace

Nueva carpeta `src/Dotar.Gateway/Application/`, namespace `Dotar.Gateway.Application`. Coherente con el layout layered
existente (`Domain/`, `Infrastructure/`, `Endpoints/`, `Dashboard/`).

```
src/Dotar.Gateway/
├── Application/                  ← NUEVA capa
│   ├── Result.cs                 (Result, Result<T>, ResultError)
│   ├── TenantAppService.cs       (servicio Scoped + inputs)
│   ├── CreateTenantInput.cs      (o dentro de TenantAppService.cs)
│   └── UpdateTenantInput.cs
├── Domain/Entities/Tenant.cs     ← +NormalizeSlug/IsValidSlug/SlugRegex
├── Endpoints/TenantApiEndpoints.cs  ← delega; mapea Result→status; quita SlugRegex/GenerateSecret
├── Dashboard/Components/Pages/Tenants.razor ← delega; aplica IsValidSlug; slug inmutable en edición
└── Program.cs                    ← +AddScoped<TenantAppService>()
```

Decisión secundaria: ¿`Result`/`ResultError` en `Application/` o en `Domain/`? Se ubican en **`Application/`** porque
el `Result` es un contrato de **orquestación de casos de uso** (categoría de error orientada a respuesta), no una
invariante de dominio. El dominio (`Tenant`) no debe conocer `ResultError`/`Conflict`/`NotFound`, que son conceptos de
caso de uso. Esto mantiene el dominio limpio.

## 4. Diagrama de dependencias (con lifetimes)

```
┌─────────────────────────────┐     ┌──────────────────────────────────┐
│ TenantApiEndpoints (handlers)│     │ Tenants.razor (componente Blazor)│
│ scope: request (Scoped)      │     │ scope: circuito SignalR (Scoped) │
└──────────────┬───────────────┘     └────────────────┬─────────────────┘
               │ inyecta                                │ @inject
               │                                        │
               ▼                                        ▼
          ┌──────────────────────────────────────────────────┐
          │  TenantAppService           [Scoped]              │
          │  + CreateAsync / UpdateAsync / UpdateTargetUrlAsync│
          │  + DeleteAsync / ToggleActiveAsync                 │
          └───┬───────────────────┬───────────────────┬───────┘
              │ inyecta           │ inyecta           │ inyecta
              ▼                   ▼                   ▼
   ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐
   │ GatewayDbContext │  │ TenantCacheService│  │ ILogger<TenantApp...>│
   │ [Scoped]         │  │ [Singleton]       │  │                      │
   └──────────────────┘  └───────┬───────────┘  └──────────────────────┘
        Scoped→Scoped: OK         │ Scoped→Singleton: OK (dirección válida)
                                  │
                                  ▼ (resuelve su propio scope)
                          IServiceScopeFactory → GatewayDbContext
                          (patrón actual del Singleton, intacto)
```

Reglas marcadas en el diagrama:

- `TenantAppService` (Scoped) → `GatewayDbContext` (Scoped): válido.
- `TenantAppService` (Scoped) → `TenantCacheService` (Singleton): válido (Scoped puede consumir Singleton).
- INVARIANTE: ningún Singleton inyecta `TenantAppService`. `TenantCacheService` sigue resolviendo su DbContext con
  `IServiceScopeFactory` (`TenantCacheService.cs:52`), sin cambios.

## 5. Estrategia de testing (TDD estricto activo)

Runner: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` (xUnit). TDD estricto: test rojo → verde →
refactor por cada incremento.

Dos niveles de red de seguridad:

1. **No-regresión (existente, no se toca)**: los ~19 tests de integración HTTP de `TenantApiEndpointsTests.cs`
   (`GatewayWebApplicationFactory` con SQLite en memoria, hosted services deshabilitados) validan el contrato HTTP
   end-to-end. DEBEN seguir verdes sin modificarse tras delegar al AppService. Cubren: auth 401, create happy path +
   secret, duplicate slug 409, slug inválido (5 variantes), normalización lowercase, url inválida, signature schemes,
   get 404, put target-url + invalidación, delete + verificación DB vacía. Son el oráculo de que el contrato no cambió.

2. **Unitarios nuevos del AppService (aislamiento)**: `TenantAppService` es testeable sin levantar HTTP porque recibe
   sus deps por DI. Estrategia:
   - `GatewayDbContext`: instancia real con `UseSqlite` en memoria (mismo enfoque que la factory de integración),
     preferido sobre mockear EF (mockear `DbSet`/`AnyAsync` es frágil). Patrón ya presente en el repo.
   - `TenantCacheService` (Singleton): es una clase concreta sin interfaz hoy. Para verificar la invalidación, dos
     opciones — (a) introducir `ITenantCacheService` y mockear, o (b) usar la instancia real con `IMemoryCache` en
     memoria y aserciones indirectas. Recomendación: **(a) extraer interfaz mínima** `ITenantCacheService` con
     `Invalidate(string)` (y `GetBySlugAsync` si interesa) para poder mockear con Moq/NSubstitute y aislar el AppService
     del estado de caché. Es el único cambio de infraestructura que el testing exige; bajo riesgo.
   - `ILogger<TenantAppService>`: `NullLogger<T>.Instance` o mock.

   Casos unitarios mínimos por operación: validación de campos vacíos, slug mal formado rechazado
   (`ResultError.Validation`), slug normalizado, unicidad (`Conflict`), FK inexistente (`Validation`), not found
   (`NotFound`), secret autogenerado vs provisto vs esquema None, invalidación de caché invocada, slug inmutable en
   update (intentar cambiarlo no tiene efecto).

3. **Bug del slug en dashboard**: como Blazor no tiene tests de integración hoy, la garantía del fix
   ("slug mal formado desde dashboard rechazado igual que API") se valida vía los tests unitarios del AppService
   (`CreateAsync` rechaza el slug inválido) más una verificación manual del flujo Blazor. El AppService es el punto
   único donde la regla se aplica, así que cubrirlo a nivel unitario cubre ambos consumidores.

## 6. Plan de migración incremental

Orden por riesgo creciente → impacto, alineado con la exploración. Cada paso es un incremento commiteable y reversible
(no se commitea en esta fase; es el plan que `sdd-tasks`/`sdd-apply` ejecutarán).

0. **Andamiaje** (test-first del contrato base): crear `Application/Result.cs` (+ `ResultError`) y la API estática de
   slug en `Tenant`. Tests unitarios de `Result` factories y de `IsValidSlug`/`NormalizeSlug`.
1. **Crear tenant** — mayor duplicación + bug del slug + tests de integración como red. Implementar `CreateAsync`
   (TDD), hacer que el endpoint `CreateTenant` (`TenantApiEndpoints.cs:64`) delegue y mapee `Result<Tenant>`→201/400/409.
   Verificar que los ~10 tests de POST siguen verdes. Recién después, hacer que `Tenants.razor SaveTenant` (rama crear)
   delegue y aplique `IsValidSlug` (cierra el bug).
2. **Editar tenant** — `UpdateAsync` (slug inmutable). Delegar `UpdateTenant` (`:152`) y la rama edición de
   `SaveTenant` (`:270`). Slug deshabilitado/ignorado en edición Blazor. Verificar tests PUT.
3. **Actualizar target-url** — `UpdateTargetUrlAsync`. Delegar `UpdateTargetUrl` (`:241`). Verificar test PUT target-url.
4. **Borrar tenant** — `DeleteAsync`. Delegar `DeleteTenant` endpoint (`:314`) y Blazor (`:311`). El diálogo de
   confirmación + count de DeliveryLogs queda en Blazor (es UX, no negocio). Verificar tests DELETE.
5. **Toggle activo** — `ToggleActiveAsync`. Solo Blazor (`:350`), sin endpoint, bajo riesgo. Blazor pasa `tenant.Slug`.

Registro DI (`Program.cs`): `builder.Services.AddScoped<TenantAppService>();` (junto a los demás services, ~línea 60).
Si se extrae `ITenantCacheService`, ajustar el registro de `TenantCacheService` a la interfaz.

Tras cada paso: eliminar la lógica/duplicación migrada del consumidor (incl. quitar `SlugRegex` y `GenerateSecret` de
`TenantApiEndpoints.cs` cuando ya no se usen). El `IServiceScopeFactory` de los endpoints/Blazor se reemplaza por
inyección directa de `TenantAppService` a medida que cada operación migra.

## 7. Decisiones registradas (ADR-style)

- **ADR-1**: `TenantAppService` Scoped (no Singleton) para DI directa de `GatewayDbContext`. Rechazado: Singleton +
  `IServiceScopeFactory` (perpetúa el patrón que queremos eliminar). Invariante: ningún Singleton lo inyecta.
- **ADR-2**: `SlugRegex` + normalización en `Domain/Tenant` (no en endpoints ni helper suelto). Rechazado: dejarlo en
  `TenantApiEndpoints` (origen del bug de divergencia) o un helper estático sin hogar conceptual.
- **ADR-3**: `Result`/`Result<T>` con `ResultError` (enum de categoría) además de mensaje. Rechazado: solo string
  (obligaría a inferir status code del texto) y librerías externas como FluentResults (dependencia innecesaria).
- **ADR-4**: Inputs propios de la capa Application (`CreateTenantInput`/`UpdateTenantInput`), no reutilizar los
  `*Request` de la capa de entrega. Rechazado: pasar los `*Request` HTTP al AppService (acopla aplicación a transporte).
  Retorno `Result<Tenant>` (entidad), no DTO de salida nuevo (ambos consumidores ya proyectan desde la entidad).
- **ADR-5**: Generación de secret de webhook dentro de `TenantAppService`. Botón "Generar" del formulario Blazor queda
  como UI (deuda menor registrada). `ApiKeyService.GenerateKey` fuera de alcance.
- **ADR-6**: Capa en `Application/` namespace `Dotar.Gateway.Application`. `Result` en Application (no Domain) por ser
  contrato de caso de uso, no invariante de dominio.

## 8. Restricciones respetadas

- No se modifica el contrato HTTP de los endpoints (status codes y payloads preservados; los tests de integración lo
  garantizan).
- TDD estricto: cada operación se implementa test-first; el AppService es mockeable por DI.
- No se commitea, no se toca `gateway.db`, no se ejecuta docker en esta fase.
- Sin paquetes NuGet nuevos. Sin migraciones EF Core (no hay cambio de esquema).
