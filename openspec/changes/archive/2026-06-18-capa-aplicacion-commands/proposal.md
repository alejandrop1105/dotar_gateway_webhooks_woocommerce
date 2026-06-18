# Proposal: Capa de Aplicación para operaciones de Tenant

## Intent

La misma operación de negocio (crear/editar/borrar/toggle tenant) está implementada DOS veces: en los Minimal API endpoints (`Endpoints/TenantApiEndpoints.cs`) y en los `@code` de `Dashboard/Components/Pages/Tenants.razor`, ambos con acceso directo a `GatewayDbContext`. Esto ya provocó un bug en producción: la validación de formato del slug (`SlugRegex`, `TenantApiEndpoints.cs:16`/`:78`) existe SOLO en la API; el dashboard normaliza pero no valida el formato, permitiendo crear slugs mal formados (`UPPER_CASE`, `with space`) desde el panel. Unificar la lógica en una única fuente de verdad elimina la divergencia y cierra el bug.

## Scope

### In Scope
- Crear `TenantAppService` (Scoped) como única fuente de verdad para tenants: crear, editar, borrar, toggle activo.
- Corregir el bug del slug: la validación `SlugRegex` se aplica también desde el dashboard (cambio de comportamiento visible y aceptado).
- Slug inmutable tras la creación: las demás propiedades quedan editables.
- Tipo `Result<T>` propio mínimo (sealed record: `IsSuccess`, `Error`, `Value`), sin librerías externas.
- API y Blazor delegan en `TenantAppService` y traducen `Result<T>` a su mecanismo nativo (API → BadRequest/Conflict/NotFound; Blazor → MudAlert).

### Out of Scope
- Políticas de reintento (`RetryPolicies.razor`) — PR posterior.
- Grupos de tenants (`TenantGroups.razor`) — PR posterior.
- Configuración Cloudflare / API Key (`Configuracion.razor`) — PR posterior.
- Cambiar el contrato HTTP de los endpoints existentes (las respuestas y status codes se preservan).

## Capabilities

### New Capabilities
- `tenant-application-service`: capa de aplicación que centraliza las reglas de negocio de tenants (validación de slug, unicidad, normalización, generación de secret, invalidación de caché) consumida tanto por la API como por el dashboard.

### Modified Capabilities
None (los endpoints existentes preservan su contrato HTTP; el cambio de comportamiento del slug es nueva validación en el dashboard, no en la API).

## Approach

Application Services planos registrados como **Scoped** (NO MediatR, NO commands/handlers a mano), coherente con `ApiKeyService`/`TenantCacheService` que ya son application services de facto. `TenantAppService` recibe `GatewayDbContext` por DI directa (elimina el patrón `IServiceScopeFactory`). Cada método async devuelve `Result<T>`. Endpoint API y componente Blazor delegan en el service: una sola fuente de verdad para las reglas.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Application/TenantAppService.cs` | New | Service Scoped con las operaciones de tenant |
| `Application/Result.cs` | New | `Result<T>` mínimo |
| `Endpoints/TenantApiEndpoints.cs` | Modified | Delega en `TenantAppService` |
| `Dashboard/Components/Pages/Tenants.razor` | Modified | Delega en `TenantAppService`; aplica `SlugRegex`; slug inmutable en edición |
| `Program.cs` (DI) | Modified | Registro Scoped del service |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Regresión en endpoints existentes | Med | ~19 tests de integración HTTP existentes son red de seguridad; deben pasar sin cambios |
| Cambio visible del slug rompe flujos del dashboard | Low | Comportamiento aceptado; cubrir con tests; slug inmutable evita caso old/new |
| Scoped no inyectable en Singletons | Med | `TenantAppService` se consume solo desde endpoints y Blazor (ambos Scoped); documentar la restricción en design |
| TDD estricto activo | — | Implementación guiada por tests (`dotnet test tests/Dotar.Gateway.Tests/...`) |

## Rollback Plan

Cada operación es un cambio aislado por método. Revertir = restaurar el `@code`/handler original que accedía directo a `GatewayDbContext` y quitar el registro del service. No hay migraciones ni cambios de esquema; `gateway.db` no se toca. Git revert del PR es suficiente.

## Dependencies

- Ninguna externa. `Result<T>` es propio. No se añaden paquetes NuGet.

## Success Criteria

- [ ] Los ~19 tests de integración HTTP existentes pasan sin modificarse.
- [ ] Crear un tenant con slug mal formado desde el dashboard es rechazado igual que en la API.
- [ ] La lógica de crear/editar/borrar/toggle tenant existe en un único lugar (`TenantAppService`).
- [ ] El slug no puede cambiarse al editar un tenant.
- [ ] No se modifica el contrato HTTP de los endpoints existentes.
