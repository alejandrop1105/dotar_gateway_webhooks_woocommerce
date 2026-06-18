# Tasks: Capa de Aplicación — Commands de Tenant (`capa-aplicacion-commands`)

## Review Workload Forecast

| Campo | Valor |
|---|---|
| Líneas estimadas (additions + deletions) | ~635 líneas |
| Riesgo presupuesto 400 líneas | High |
| PRs encadenados recomendados | Yes |
| Split sugerido | PR 1: andamiaje + crear · PR 2: editar + target-url · PR 3: borrar + toggle + limpieza |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending (esperando elección del usuario) |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Unidades de trabajo sugeridas

| Unidad | Objetivo | PR sugerido | Notas |
|---|---|---|---|
| A | Andamiaje (Result, SlugAPI, ITenantCacheService) + CreateAsync + endpoint crear + Blazor crear | PR 1 | Base del resto; cierra bug del slug en dashboard; ~315 líneas |
| B | UpdateAsync + endpoint editar + Blazor editar + UpdateTargetUrlAsync + endpoint target-url | PR 2 | Depende de PR 1; ~150 líneas |
| C | DeleteAsync + endpoint borrar + Blazor borrar + ToggleActiveAsync + Blazor toggle + limpieza global | PR 3 | Depende de PR 2; ~170 líneas |

---

## Fase 0 — Andamiaje (tipos base + infraestructura de testing)

> Requisitos cubiertos: Tipo Result, Crear Tenant (validación de slug), extracción `ITenantCacheService`.
> Estas tareas no tienen dependencias previas; pueden comenzar de inmediato.

- [x] **0.1** [RED] Crear `tests/Dotar.Gateway.Tests/Application/ResultTests.cs`: tests que verifican `Result.Success()`, `Result.Failure(ResultError.Validation, msg)`, `Result.NotFound`, `Result.Conflict`, `Result<T>.Success(value)`, `Result<T>.Failure` y que `IsSuccess`/`Error`/`Message`/`Value` son correctos en cada factory. Ejecutar: deben fallar (`dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`).

- [x] **0.2** [GREEN] Crear `src/Dotar.Gateway/Application/Result.cs` con `ResultError` (enum: None/Validation/NotFound/Conflict), `Result` (sealed record, factories `Success/Failure/Validation/NotFound/Conflict`) y `Result<T>` (ídem con propiedad `Value`). Firmas exactas según diseño §3. Verificar: tests 0.1 pasan.

- [x] **0.3** [RED] Crear `tests/Dotar.Gateway.Tests/Domain/TenantSlugTests.cs`: tests que verifican `Tenant.NormalizeSlug(" Mi-Tenant ")` → `"mi-tenant"`, `Tenant.IsValidSlug("mi-tenant")` → `true`, `Tenant.IsValidSlug("with space")` → `false`, `Tenant.IsValidSlug("UPPER")` → `false`, `Tenant.IsValidSlug("a")` → `true` (slug de un carácter), `Tenant.IsValidSlug("")` → `false`. Ejecutar: deben fallar.

- [x] **0.4** [GREEN] Convertir `src/Dotar.Gateway/Domain/Entities/Tenant.cs` a `partial class` y agregar segundo archivo parcial `Tenant.Slug.cs` (o métodos estáticos inline en la misma clase) con `SlugRegex` (`[GeneratedRegex]` o `static readonly Regex` con patrón idéntico a `TenantApiEndpoints.cs:16`), `NormalizeSlug(string raw)` y `IsValidSlug(string normalizedSlug)`. Verificar: tests 0.3 pasan y los ~19 tests de integración HTTP siguen verdes.

- [x] **0.5** [RED] Crear `tests/Dotar.Gateway.Tests/Application/TenantAppServiceCacheTests.cs` (clase stub o test de contrato de interfaz): un test que verifique que `ITenantCacheService.Invalidate(slug)` es invocable por mock (Moq/NSubstitute). Propósito: confirmar que la interfaz existe y el mock funciona antes de usarlo en tests del service.

- [x] **0.6** [GREEN] Crear `src/Dotar.Gateway/Infrastructure/ITenantCacheService.cs` con interfaz mínima: `void Invalidate(string slug)` y `Task<Tenant?> GetBySlugAsync(string slug)`. Implementar la interfaz en `TenantCacheService` (`: ITenantCacheService`). Actualizar registro DI en `Program.cs`: `AddSingleton<TenantCacheService>` → `AddSingleton<ITenantCacheService, TenantCacheService>`. Actualizar todos los consumidores que inyectan `TenantCacheService` directamente (verificar: `IngestEndpoints` u otros) para depender de `ITenantCacheService`. Verificar: tests 0.5 pasan, ~19 tests HTTP siguen verdes, compilación sin errores.

---

## Fase 1 — Crear Tenant (TDD, slice PR 1)

> Requisitos cubiertos: Crear Tenant (campos requeridos, normalización, unicidad, secret, caché), preservación contrato HTTP.

- [x] **1.1** [RED] Agregar tests unitarios en `tests/Dotar.Gateway.Tests/Application/TenantAppServiceCreateTests.cs`: campos vacíos → `ResultError.Validation`; slug inválido post-normalización → `ResultError.Validation`; slug duplicado → `ResultError.Conflict`; FK `RetryPolicyId` inexistente → `ResultError.Validation`; FK `TenantGroupId` inexistente → `ResultError.Validation`; slug normalizado almacenado (input `" Mi-Tenant "` → slug `"mi-tenant"`); `SignatureScheme != None` → `WebhookSecret` base64 32 bytes; `SignatureScheme = None` → `WebhookSecret` vacío; secret provisto explícitamente → se usa el provisto; `ITenantCacheService.Invalidate(slug)` invocado exactamente una vez tras creación exitosa. Usar `GatewayDbContext` con `UseSqlite(":memory:")` + mock de `ITenantCacheService`. Ejecutar: deben fallar.

- [x] **1.2** [GREEN] Crear `src/Dotar.Gateway/Application/TenantAppService.cs` con clase `TenantAppService` (Scoped, constructor: `GatewayDbContext db, ITenantCacheService cache, ILogger<TenantAppService> logger`), records `CreateTenantInput` y `UpdateTenantInput` (firmas exactas del diseño §4; `UpdateTenantInput` SIN campo Slug), helper privado `GenerateWebhookSecret()` (32 bytes base64), e implementación completa de `CreateAsync(CreateTenantInput input)`. Verificar: tests 1.1 pasan.

- [x] **1.3** Registrar `TenantAppService` en `Program.cs`: `builder.Services.AddScoped<TenantAppService>();` (junto a los demás services, antes de `app.Build()`). Compilar y verificar que los ~19 tests HTTP siguen verdes.

- [x] **1.4** Modificar handler `CreateTenant` en `src/Dotar.Gateway/Endpoints/TenantApiEndpoints.cs` (hoy `:64`): recibir `TenantAppService` por parámetro de handler (eliminar `IServiceScopeFactory` de ese handler si ya no lo usa), construir `CreateTenantInput` desde el `CreateTenantRequest` recibido, delegar en `appService.CreateAsync(input)`, mapear `Result<Tenant>` → `201 Created` / `400 BadRequest` / `409 Conflict` usando `result.Error` (categoría). Mantener el payload de respuesta idéntico al actual. Verificar: todos los tests de `POST /api/tenants` (~10) siguen verdes.

- [x] **1.5** Modificar la rama **crear** de `SaveTenant` en `src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor` (hoy `:270`, rama `_editingTenant == null`): inyectar `TenantAppService` vía `@inject`, construir `CreateTenantInput` desde `TenantFormModel`, delegar en `appService.CreateAsync(input)`, aplicar `Tenant.IsValidSlug(Tenant.NormalizeSlug(slug))` para mostrar error de slug antes de delegar (cierra el bug: dashboard ya no acepta slugs que la API rechazaría). En error, mostrar `result.Message` en `_errorMessage`/`MudAlert`. Eliminar `IServiceScopeFactory` del componente si esta era su única operación con scope propio. Verificar: compilación limpia; tests 1.1 siguen verdes.

---

## Fase 2 — Editar Tenant (slice PR 2)

> Requisitos cubiertos: Editar Tenant (slug inmutable, propiedades editables, UpdatedAt, caché, preservación HTTP).

- [x] **2.1** [RED] Crear `tests/Dotar.Gateway.Tests/Application/TenantAppServiceUpdateTests.cs`: slug no encontrado → `ResultError.NotFound`; `TargetUrl` vacía → `ResultError.Validation`; `TargetUrl` inválida → `ResultError.Validation`; edición exitosa → `IsSuccess = true`, `UpdatedAt` actualizado a UTC ≥ tiempo antes de la llamada; `ITenantCacheService.Invalidate(slug)` invocado exactamente una vez; el slug del tenant no cambia (slug inmutable — verificar que `UpdateTenantInput` no acepta slug y el tenant en DB mantiene el slug original). Ejecutar: deben fallar.

- [x] **2.2** [GREEN] Implementar `UpdateAsync(string slug, UpdateTenantInput input)` en `TenantAppService.cs`: `NormalizeSlug` del slug de identificación, buscar tenant, 404 si no existe, validar `TargetUrl` (no vacía, `Uri.TryCreate` con `UriKind.Absolute`), actualizar propiedades no-null de `UpdateTenantInput` (excepto slug — no existe en el record), setear `UpdatedAt = DateTime.UtcNow`, guardar, invalidar caché. Verificar: tests 2.1 pasan.

- [x] **2.3** Modificar handler `UpdateTenant` en `TenantApiEndpoints.cs` (hoy `:152`): recibir `TenantAppService` por parámetro, construir `UpdateTenantInput` desde `UpdateTenantRequest` (sin slug), delegar en `UpdateAsync`, mapear `Result<Tenant>` → 200/400/404. Verificar: tests de `PUT /api/tenants/{slug}` siguen verdes.

- [x] **2.4** Modificar la rama **editar** de `SaveTenant` en `Tenants.razor`: construir `UpdateTenantInput` desde `TenantFormModel` (sin campo slug — el slug es el del tenant que se está editando, inmutable), delegar en `appService.UpdateAsync(slug, input)`, mostrar error en `_errorMessage` si falla. El campo slug del formulario debe quedar deshabilitado (readonly) cuando se edita. Verificar: compilación limpia.

---

## Fase 3 — Actualizar Target URL (slice PR 2, continúa)

> Requisito cubierto: Preservación contrato HTTP para `PUT /{slug}/target-url`.

- [x] **3.1** [RED] Agregar tests en `tests/Dotar.Gateway.Tests/Application/TenantAppServiceUpdateTargetUrlTests.cs`: slug no encontrado → `NotFound`; URL vacía → `Validation`; URL inválida → `Validation`; actualización exitosa → `IsSuccess = true`, `UpdatedAt` actualizado, caché invalidada. Ejecutar: deben fallar.

- [x] **3.2** [GREEN] Implementar `UpdateTargetUrlAsync(string slug, string targetUrl)` en `TenantAppService.cs`: buscar por slug normalizado, 404 si no existe, validar URL (no vacía + `Uri.TryCreate` absoluta), actualizar `TargetUrl` y `UpdatedAt`, guardar, invalidar caché. Verificar: tests 3.1 pasan.

- [x] **3.3** Modificar handler `UpdateTargetUrl` en `TenantApiEndpoints.cs` (hoy `:241`): delegar en `appService.UpdateTargetUrlAsync(slug, targetUrl)`, mapear result → 200/400/404. Verificar: test de `PUT /api/tenants/{slug}/target-url` sigue verde.

---

## Fase 4 — Borrar Tenant (slice PR 3)

> Requisitos cubiertos: Borrar Tenant (existencia, caché, preservación HTTP).

- [x] **4.1** [RED] Crear `tests/Dotar.Gateway.Tests/Application/TenantAppServiceDeleteTests.cs`: slug no encontrado → `NotFound`; borrado exitoso → `IsSuccess = true`, caché invalidada; tenant ya no existe en DB tras borrado. Ejecutar: deben fallar.

- [x] **4.2** [GREEN] Implementar `DeleteAsync(string slug)` en `TenantAppService.cs`: buscar por slug normalizado (incluir cascada vía `DbContext` o confiar en la configuración de FK existente), 404 si no existe, `_db.Tenants.Remove(tenant)`, guardar, invalidar caché. Verificar: tests 4.1 pasan.

- [x] **4.3** Modificar handler `DeleteTenant` en `TenantApiEndpoints.cs` (hoy `:314`): delegar en `appService.DeleteAsync(slug)`, mapear result → 200/404. Verificar: test de `DELETE /api/tenants/{slug}` sigue verde.

- [x] **4.4** Modificar `DeleteTenant` en `Tenants.razor` (hoy `:311`): delegar en `appService.DeleteAsync(tenant.Slug)`, mostrar error si falla. El diálogo de confirmación + count de `DeliveryLogs` permanece en Blazor (UX, no negocio). Verificar: compilación limpia.

---

## Fase 5 — Toggle Activo (slice PR 3, continúa)

> Requisitos cubiertos: Toggle activo (IsActive, UpdatedAt, caché, not found).

- [x] **5.1** [RED] Crear `tests/Dotar.Gateway.Tests/Application/TenantAppServiceToggleTests.cs`: slug no encontrado → `NotFound`; toggle activo→inactivo → `IsSuccess = true`, `IsActive = false`, `UpdatedAt` actualizado, caché invalidada; toggle inactivo→activo → `IsActive = true`. Ejecutar: deben fallar.

- [x] **5.2** [GREEN] Implementar `ToggleActiveAsync(string slug)` en `TenantAppService.cs`: buscar por slug normalizado, 404 si no existe, `tenant.IsActive = !tenant.IsActive`, `UpdatedAt = DateTime.UtcNow`, guardar, invalidar caché, devolver `Result<Tenant>.Success(tenant)`. Verificar: tests 5.1 pasan.

- [x] **5.3** Modificar `ToggleTenantActive` en `Tenants.razor` (hoy `:350`): pasar `tenant.Slug` (no `tenant.Id`), delegar en `appService.ToggleActiveAsync(tenant.Slug)`, mostrar error si falla. Verificar: compilación limpia, ~19 tests HTTP siguen verdes.

---

## Fase 6 — Limpieza y cierre (slice PR 3, continúa)

> Garantiza que no queden duplicados y que los consumidores dependan únicamente del AppService.

- [x] **6.1** Eliminar `SlugRegex` de `TenantApiEndpoints.cs` (hoy `:16`) una vez que ningún handler lo referencie (confirmado tras pasos 1.4, 2.3, 3.3). Verificar: compilación limpia.

- [x] **6.2** Eliminar `GenerateSecret` de `TenantApiEndpoints.cs` (hoy `:337`) una vez que el handler `CreateTenant` delegue en `TenantAppService.CreateAsync` (paso 1.4 ya lo hizo). Verificar: compilación limpia.

- [x] **6.3** Eliminar `IServiceScopeFactory` de `TenantApiEndpoints.cs` cuando todos los handlers de tenant ya inyecten `TenantAppService` directamente (tras pasos 1.4, 2.3, 3.3, 4.3). Si `IServiceScopeFactory` aún se usa por otro motivo no relacionado a tenant, conservarlo. Verificar: compilación limpia.

- [x] **6.4** ~~Eliminar `@inject IServiceScopeFactory ScopeFactory` de `Tenants.razor`~~ — **NO aplicado (revertido por review adversarial)**: en Blazor Server `IServiceScopeFactory` es necesario para crear un scope corto por operación y evitar el anti-patrón de `DbContext` con vida de circuito (change-tracker corrupto, datos stale). Se conserva a propósito; cada operación usa `using var scope = ScopeFactory.CreateScope()` resolviendo `TenantAppService`/`GatewayDbContext` adentro. La delegación en `TenantAppService` se preserva (no hay lógica de negocio inline en el `.razor`).

- [x] **6.5** Ejecución final de la suite completa: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`. Verificar: todos los tests pasan (incluyendo los ~19 de integración HTTP sin modificación y los nuevos unitarios de cada fase). Documentar el recuento final de tests en el commit de cierre.

---

## Notas de implementación

- **TDD estricto**: cada tarea marcada [RED] escribe los tests primero. La tarea [GREEN] inmediatamente siguiente los hace pasar. No avanzar a la siguiente operación hasta tener verde.
- **No-regresión**: los ~19 tests HTTP de `TenantApiEndpointsTests.cs` NO deben modificarse. Son el oráculo permanente del contrato HTTP.
- **Slug inmutable por construcción**: `UpdateTenantInput` no tiene campo `Slug`. No validar ni ignorar en runtime — simplemente no existe en el contrato.
- **ITenantCacheService**: extraída en la Fase 0 y usada como mock en todos los tests unitarios del AppService.
- **Sin NuGet nuevos, sin migraciones EF Core, sin cambio de contrato HTTP.**
