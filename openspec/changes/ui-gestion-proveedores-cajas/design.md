# Design: UI de gestión de proveedores y padrón de cajas

## Technical Approach

Se agregan dos páginas Blazor Server (`/proveedores`, `/cajas`) y un dialog "Cambiar credenciales", siguiendo el molde EXACTO de `Tenants.razor`: routing multi-`@page`, form model interno, acceso a datos vía `IServiceScopeFactory` con scope corto por operación, `ConfirmDialog.razor` reutilizable e `ISnackbar`. Ambos AppServices se extienden SOLO con métodos nuevos (lectura + borrado + hint enmascarado); las firmas existentes (`UpsertAsync`, `RegistrarAsync`, lookups, anti-SSRF) no se tocan. Sin migración EF: las entidades y DbSets ya existen en prod (v1.2.0).

## Architecture Decisions

| # | Decisión | Choice | Rationale |
|---|----------|--------|-----------|
| 1 | Acceso a datos | AppServices + `GatewayDbContext` vía `ScopeFactory.CreateScope()` por operación; NO consumir Admin API REST | Idéntico a `Tenants.razor`. Evita exponer la API Key en el bundle del cliente y evita DbContext circuit-scoped (anti-patrón). |
| 2 | Hint de credenciales | Nuevo `ListMetadataConHintAsync(tenantId?)` → `ProveedorMetadataDto` con `AccessTokenHint`/`SigningSecretHint`. Descifra server-side con el `IDataProtector` existente, toma últimos **6** chars, devuelve `"••••••" + sufijo` | El secret completo nunca sale del servidor. Solo el hint viaja al cliente. Reusa `Descifrar()` privado existente. |
| 3 | Cambiar credenciales | `CambiarCredencialesDialog.razor` (MudDialog separado) que re-ingresa accessToken/signingSecret y llama a `UpsertAsync` con el JSON re-armado; nunca pre-puebla valores | `UpsertAsync` ya re-cifra de forma idempotente por `(TenantId, ProveedorNombre)`. No hace falta método nuevo de re-cifrado. Form de metadata y form de credenciales quedan separados. |
| 4 | Listado de cajas | `MudTable` con **server-side pagination** (`ServerData`) sobre `ListByTenantAsync(tenantId, skip, take, total)` | El padrón puede crecer (1 fila por caja activa por tenant). Server-side evita traer todo a memoria; el proyecto ya usa MudBlazor v9. `.Take(N)` ocultaría cajas. |
| 5 | Advertencia de borrado | Mostrar warning si `UltimaVez >= UtcNow - CajaTtlMinutos` (TTL configurado, default 30 min) | Misma ventana que usa el cache service para considerar una caja "viva". Si está dentro del TTL, el ERP la re-registrará en el próximo heartbeat → el dialog avisa que "volverá al padrón". |
| 6 | Navegación | `MudNavGroup` "Ruteo Multitenant" en `NavMenu.razor` con `/proveedores` y `/cajas`. En `Tenants.razor`, `MudIconButton` (`Icons.Material.Filled.Webhook`) en la fila → `NavigationManager.NavigateTo($"/proveedores?tenant={t.Slug}")` | `MudNavGroup` agrupa sin romper el menú actual. El query param `tenant` permite que `/proveedores` pre-filtre por slug sin acoplar componentes ni abrir un dialog cross-página. |
| 7 | Seguridad / anti-patrones | Reusar anti-SSRF de `RegistrarAsync` (no duplicar en UI); credenciales nunca en claro al cliente (solo hint); scope corto, sin DbContext de circuito | El cambio es solo UI + lectura/borrado. La validación de callbackUrl ya vive en el AppService y no se invoca desde estas páginas (no hay registro desde UI). |

## Data Flow

    /proveedores  ─▶ ProveedorWebhookConfigAppService.ListMetadataConHintAsync()
                       │ Unprotect (server) ─▶ sufijo 6 chars ─▶ "••••••a3f2"
                       └─▶ ProveedorMetadataDto (SIN secret) ─▶ cliente

    Dialog "Cambiar credenciales" ─▶ UpsertAsync(tenantId, proveedor, jsonCreds) ─▶ Protect ─▶ DB

    /cajas (MudTable ServerData) ─▶ CajaRegistradaAppService.ListByTenantAsync(skip,take)
                                     borrar ─▶ ConfirmDialog (+warning si UltimaVez < TTL) ─▶ DeleteAsync

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `src/Dotar.Gateway/Dashboard/Components/Pages/Proveedores.razor` | Create | `@page "/proveedores"`, `/proveedores/create`, `/proveedores/{Id:long}/edit`. Lista metadata + hint, CRUD metadata, botón "Cambiar credenciales", filtro por `?tenant={slug}`. |
| `src/Dotar.Gateway/Dashboard/Components/Pages/Cajas.razor` | Create | `@page "/cajas"`. `MudTable` server-side por tenant (MudSelect de tenant), borrar/revocar con advertencia de heartbeat. |
| `src/Dotar.Gateway/Dashboard/Components/Shared/CambiarCredencialesDialog.razor` | Create | MudDialog: campos accessToken/signingSecret (Password+toggle), nunca pre-poblado; arma JSON y llama `UpsertAsync`. |
| `src/Dotar.Gateway/Dashboard/Components/Layout/NavMenu.razor` | Modify | Agregar `MudNavGroup` "Ruteo Multitenant" con `/proveedores` y `/cajas`. |
| `src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor` | Modify | `MudIconButton` en fila → `/proveedores?tenant={Slug}`. |
| `src/Dotar.Gateway/Application/ProveedorWebhookConfigAppService.cs` | Modify | Agregar `ProveedorMetadataDto` + `ListMetadataConHintAsync(int? tenantId)` + `DeleteAsync(int tenantId, string proveedorNombre)`. Helper `Hint(string)`. NO tocar firmas existentes. |
| `src/Dotar.Gateway/Application/CajaRegistradaAppService.cs` | Modify | Agregar `ListByTenantAsync(int tenantId, int skip, int take)` → `(IReadOnlyList<CajaDto>, int total)` + `DeleteAsync(long cajaId)` (invalida caché). NO tocar `RegistrarAsync`. |

## Interfaces / Contracts

```csharp
// ProveedorWebhookConfigAppService.cs — DTO sin secretos en claro
public sealed record ProveedorMetadataDto(
    long Id, int TenantId, string ProveedorNombre, string CuentaExternaId,
    string BaseUrl, bool IsActive,
    string AccessTokenHint,   // "••••••" + últimos 6
    string SigningSecretHint, // "••••••" + últimos 6
    DateTime UpdatedAt);

Task<IReadOnlyList<ProveedorMetadataDto>> ListMetadataConHintAsync(int? tenantId = null);
Task<Result> DeleteAsync(int tenantId, string proveedorNombre);
// Hint: s.Length <= 6 ? "••••••" : "••••••" + s[^6..]

// CajaRegistradaAppService.cs
Task<(IReadOnlyList<CajaDto> Items, int Total)> ListByTenantAsync(int tenantId, int skip, int take);
Task<Result> DeleteAsync(long cajaId); // invalida caché vía _cache.Invalidate(tenantId, identificador)
```

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | `ListMetadataConHintAsync` enmascara correctamente (>6, =6, <6 chars), nunca expone secret; `DeleteAsync` proveedor; hint usa `Descifrar` real | xUnit + `IDataProtectionProvider` ephemeral + SQLite in-memory (patrón de `ProveedorConfigApiEndpointsTests`/`CajaRegistradaAppServiceTests`). |
| Unit | `CajaRegistradaAppService.ListByTenantAsync` paginación + total; `DeleteAsync` invalida caché | xUnit con `IConfiguration` in-memory (`CajaTtlMinutos=30`) y `ICajaRegistradaCacheService` fake/spy. |
| Component | Hint enmascarado renderiza; dialog credenciales no pre-puebla; warning de heartbeat aparece dentro del TTL | bUnit si está disponible; si no, smoke manual en `/proveedores` y `/cajas` (sin infra de tests de componentes en el repo hoy). |
| Regression | Ingesta 1-a-1, `IngestEndpoints`, Admin API y `RegistrarAsync` intactos | `dotnet test` completo — ningún test existente debe romperse. |

## Migration / Rollout

No migration required. Las entidades `ProveedorWebhookConfig` y `CajaRegistrada` y sus DbSets ya existen en prod (v1.2.0). El cambio es solo UI + métodos de servicio nuevos. **Rollback**: revertir el PR elimina páginas, dialog y métodos nuevos sin afectar ingesta, Admin API ni el padrón; la config de proveedor sigue administrable por API REST.

## Open Questions

- [ ] ¿Existe infraestructura bUnit en el repo para tests de componentes, o se cubre solo con tests de AppService + smoke manual?
