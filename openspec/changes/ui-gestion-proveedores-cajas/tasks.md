# Tasks: ui-gestion-proveedores-cajas

> Desglose ordenado en slices autónomos. Convención: RED → GREEN para cada método de AppService con test xUnit; componentes UI sin test automatizado (no hay bUnit en el repo — smoke manual).
>
> Nota sobre umbral del warning de revocación: el spec textual de `ui-padron-cajas` menciona "10 minutos", pero la decisión fijada en la tarea (coherente con la ventana del caché) es leer `Seguridad:CajaTtlMinutos` (IConfiguration, default 30 min). Las tasks implementan la decisión acordada, NO el valor hardcodeado del spec.

---

## Slice 1 — ProveedorWebhookConfigAppService: DTO + ListarMetadataAsync

**Requisito(s):** `proveedor-webhook-config-service` → "Listar configuraciones con hint enmascarado"; `ui-config-proveedor` → "Listado con hint enmascarado"; "No modificar firmas existentes".

**Dependencias:** ninguna (punto de entrada).

**Modo TDD:** ESTRICTO (métodos nuevos de AppService).

### T-01 — [x] [RED] Tests de hint y ListarMetadataAsync

Archivo de test: `tests/.../ProveedorWebhookConfigAppServiceTests.cs` (nuevo o existente).

Escribir los tests ANTES del método. Cubrir:

- `HintCredenciales` formato `••••••XXXXXX` cuando accessToken descifrado > 6 chars.
- `HintCredenciales` = `••••••` + todos los chars cuando descifrado < 6 chars.
- `HintCredenciales` = `••••••??????` cuando el descifrado falla (clave rotada / dato corrupto); no se lanza excepción al caller; el error se loguea.
- `ListarMetadataAsync()` con registros retorna una entrada por config, con campo `TenantNombre` (join con Tenants) y sin exponer `CredencialesCifradas`, `AccessToken` ni `SigningSecret`.
- `ListarMetadataAsync()` sin registros retorna lista vacía sin excepción.
- Los tests usarán `IDataProtectionProvider` ephemeral + SQLite in-memory (patrón de tests existentes en el repo).

Estado de todos los tests al finalizar este task: **ROJO** (los métodos aún no existen).

---

### T-02 — [x] [GREEN] ProveedorMetadataDto + helper Hint + ListarMetadataAsync

Archivo: `src/Dotar.Gateway/Application/ProveedorWebhookConfigAppService.cs`.

Implementar sin tocar ninguna firma existente:

1. `ProveedorConfigMetadataDto` (sealed record) con los campos del spec:
   `Id, TenantId, TenantNombre, ProveedorNombre, CuentaExternaId, BaseUrl, IsActive, HintCredenciales, CreatedAt, UpdatedAt`.
   > El campo se llama `HintCredenciales` (un solo campo que expone el hint del accessToken) conforme al spec de servicio. El design usa `AccessTokenHint`/`SigningSecretHint` separados; el spec de servicio es la autoridad — un solo campo `HintCredenciales`.

2. Helper privado `Hint(string descifrado)`:
   - `descifrado.Length <= 6` → `"••••••" + descifrado`
   - else → `"••••••" + descifrado[^6..]`

3. `ListarMetadataAsync()` (sin parámetros conforme spec de servicio):
   - Join `ProveedorWebhookConfigs` con `Tenants` para obtener `TenantNombre`.
   - Por cada config: intentar `Descifrar()` del accessToken (método privado existente). Si falla → hint `"••••••??????"`, loguear sin relanzar.
   - Proyectar a `ProveedorConfigMetadataDto`.
   - Retornar `List<ProveedorConfigMetadataDto>`.

Todos los tests del T-01 deben pasar en **VERDE** al finalizar.

**Verificar:** `dotnet test` completo sin nuevas fallas.

---

## Slice 2 — ProveedorWebhookConfigAppService: EliminarAsync

**Requisito(s):** `proveedor-webhook-config-service` → "Eliminar configuración de proveedor por Id".

**Dependencias:** T-02 (DTO ya existe; el AppService ya compiló con el método nuevo del slice 1).

**Modo TDD:** ESTRICTO.

### T-03 — [x] [RED] Tests de EliminarAsync (proveedor)

En el mismo archivo de test:

- `EliminarAsync(id)` con Id existente → elimina de DB + retorna `Result.Success()`.
- `EliminarAsync(id)` con Id inexistente → retorna `Result.Failure(ResultError.NotFound, ...)` sin excepción.
- No toca entidades `CajaRegistrada` (verificar que la tabla de cajas no se altera).

Estado al finalizar: **ROJO**.

---

### T-04 — [x] [GREEN] EliminarAsync (proveedor)

Archivo: `src/Dotar.Gateway/Application/ProveedorWebhookConfigAppService.cs`.

Implementar `Task<Result> EliminarAsync(long id)`:
- Buscar por Id.
- Not found → `Result.Failure(ResultError.NotFound, ...)`.
- Found → `_db.Remove(config)` + `SaveChangesAsync()` → `Result.Success()`.
- No tocar `UpsertAsync`, `GetByProveedorYCuentaAsync`, `GetByTenantYProveedorAsync`, `GetCompletoByProveedorYCuentaAsync`.

Todos los tests del T-03 deben pasar en **VERDE** al finalizar.

**Verificar:** `dotnet test` completo.

---

## Slice 3 — CajaRegistradaAppService: ListarPorTenantAsync

**Requisito(s):** `caja-registrada-service` → "Listar cajas por tenant"; `ui-padron-cajas` → "Listado por tenant".

**Dependencias:** ninguna (paralelo con Slice 1/2 si hay segunda pista de trabajo, pero secuencial si es un único dev — comenzar luego de T-04 para evitar conflictos de compilación).

**Modo TDD:** ESTRICTO.

### T-05 — [x] [RED] Tests de ListarPorTenantAsync

Archivo de test: `tests/.../CajaRegistradaAppServiceTests.cs` (nuevo o existente).

Cubrir:

- `ListarPorTenantAsync(tenantId)` retorna solo cajas del tenant X.
- `ListarPorTenantAsync(null)` retorna cajas de todos los tenants.
- Tenant sin cajas → lista vacía sin excepción.
- El tipo retornado es `List<CajaDto>` usando el DTO ya existente en el servicio.
- Usar SQLite in-memory + `IConfiguration` in-memory con `Seguridad:CajaTtlMinutos=30`.

Estado: **ROJO**.

---

### T-06 — [x] [GREEN] ListarPorTenantAsync

Archivo: `src/Dotar.Gateway/Application/CajaRegistradaAppService.cs`.

Implementar `Task<List<CajaDto>> ListarPorTenantAsync(int? tenantId)`:
- `tenantId != null` → filtrar `CajasRegistradas.Where(c => c.TenantId == tenantId)`.
- `tenantId == null` → sin filtro.
- Proyectar a `CajaDto` (Id, TenantId, Identificador, CallbackUrl, UltimaVez, CreatedAt, UpdatedAt).
- No tocar `RegistrarAsync`.

Tests T-05 en **VERDE** al finalizar.

**Verificar:** `dotnet test` completo.

---

## Slice 4 — CajaRegistradaAppService: RevocarAsync

**Requisito(s):** `caja-registrada-service` → "Revocar caja por Id"; `ui-padron-cajas` → "Revocar con advertencia".

**Dependencias:** T-06.

**Modo TDD:** ESTRICTO.

### T-07 — [x] [RED] Tests de RevocarAsync

En el mismo archivo de test de cajas:

- `RevocarAsync(id)` con Id existente → elimina de DB, invalida caché para `(TenantId, Identificador)` de esa caja, retorna `Result.Success()`.
- `RevocarAsync(id)` con Id inexistente → `Result.Failure(ResultError.NotFound, ...)` sin excepción; cache de otras cajas no se modifica.
- Múltiples cajas del mismo tenant: al revocar una, las demás siguen resolviendo (usar spy/fake de `ICajaRegistradaCacheService`).
- `RegistrarAsync` no se ve afectado (verificar que firma y comportamiento son idénticos).

Estado: **ROJO**.

---

### T-08 — [x] [GREEN] RevocarAsync

Archivo: `src/Dotar.Gateway/Application/CajaRegistradaAppService.cs`.

Implementar `Task<Result> RevocarAsync(long id)`:
- Buscar caja por Id.
- Not found → `Result.Failure(ResultError.NotFound, ...)`.
- Found → `_db.Remove(caja)` + `SaveChangesAsync()` + `_cache.Invalidate(caja.TenantId, caja.Identificador)` → `Result.Success()`.
- No tocar `RegistrarAsync` ni sus validaciones anti-SSRF.

Tests T-07 en **VERDE** al finalizar.

**Verificar:** `dotnet test` completo.

---

## Slice 5 — NavMenu: MudNavGroup "Ruteo Multitenant"

**Requisito(s):** `ui-config-proveedor` y `ui-padron-cajas` → navegación; `design.md` decisión 6.

**Dependencias:** ninguna de código (paralelo posible con slices 1-4 si se trabaja en ramas separadas; mejor después de T-08 en rama única para evitar conflictos).

**Modo TDD:** sin test automatizado (no hay bUnit; smoke manual en el dashboard).

### T-09 — [x] NavMenu.razor: agregar grupo de navegación

Archivo: `src/Dotar.Gateway/Dashboard/Components/Layout/NavMenu.razor`.

- Agregar `MudNavGroup` "Ruteo Multitenant" con:
  - `MudNavLink` → `/proveedores` (ícono sugerido: `Webhook` o similar de MUI).
  - `MudNavLink` → `/cajas` (ícono sugerido: `PointOfSale` o similar).
- No modificar ni reordenar grupos existentes.

**Smoke:** el menú lateral muestra el grupo con los dos links sin romper la navegación actual.

---

## Slice 6 — Tenants.razor: botón de acceso a config de proveedor

**Requisito(s):** `ui-config-proveedor` → "Acceso desde fila de tenant en /tenants".

**Dependencias:** T-09 (NavMenu ya instalado; `/proveedores` existirá en T-10).

**Modo TDD:** sin test automatizado; smoke manual.

### T-10 — [x] Tenants.razor: MudIconButton → /proveedores?tenant={Slug}

Archivo: `src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor`.

- En la fila de cada tenant, agregar `MudIconButton` con ícono `Icons.Material.Filled.Webhook`.
- Al hacer clic: `NavigationManager.NavigateTo($"/proveedores?tenant={t.Slug}")`.
- No alterar otras acciones de la fila ni el resto del componente.

**Smoke:** desde `/tenants`, el botón navega a `/proveedores` con el query param correcto.

---

## Slice 7 — CambiarCredencialesDialog.razor

**Requisito(s):** `ui-config-proveedor` → "Dialog separado Cambiar credenciales".

**Dependencias:** AppServices de proveedores (T-04) deben compilar. El dialog llama a `UpsertAsync` (ya existente).

**Modo TDD:** sin test automatizado; smoke manual.

### T-11 — [x] CambiarCredencialesDialog.razor (componente nuevo)

Archivo nuevo: `src/Dotar.Gateway/Dashboard/Components/Shared/CambiarCredencialesDialog.razor`.

Implementar:

- `MudDialog` con parámetros: `TenantId`, `ProveedorNombre`, `CuentaExternaId`, `BaseUrl`, `IsActive`, `HintActual` (solo lectura, referencia visual), `OnSuccess` (callback).
- Campos de formulario:
  - `AccessToken` (MudTextField tipo Password + toggle de visibilidad). **Nunca pre-poblado.**
  - `SigningSecret` (MudTextField tipo Password + toggle). **Nunca pre-poblado.**
- Validación: ambos campos requeridos antes de habilitar "Confirmar".
- Al confirmar: armar JSON de credenciales y llamar `UpsertAsync(TenantId, ProveedorNombre, CuentaExternaId, BaseUrl, IsActive, accessToken, signingSecret)`.
- Mostrar snackbar de éxito + invocar `OnSuccess` / cerrar dialog.
- Acceso a `ProveedorWebhookConfigAppService` vía `IServiceScopeFactory.CreateScope()` (no inyección directa).

**Smoke:** abrir dialog desde `/proveedores`, verificar que los campos están vacíos; ingresar credenciales válidas; verificar snackbar y que el hint en tabla se actualiza al recargar.

---

## Slice 8 — Proveedores.razor (página principal)

**Requisito(s):** `ui-config-proveedor` completo → listado, crear, editar metadata, activar/desactivar, eliminar, filtro por tenant, scope corto.

**Dependencias:** T-02 (`ListarMetadataAsync`), T-04 (`EliminarAsync`), T-11 (`CambiarCredencialesDialog`).

**Modo TDD:** sin test automatizado; smoke manual.

### T-12 — [x] Proveedores.razor (componente nuevo)

Archivo nuevo: `src/Dotar.Gateway/Dashboard/Components/Pages/Proveedores.razor`.

Routing: `@page "/proveedores"`, `@page "/proveedores/create"`, `@page "/proveedores/{Id:long}/edit"`.

Implementar en orden lógico dentro del componente:

**A. Estado y listado**
- `OnInitializedAsync`: leer query param `?tenant={slug}` para pre-filtrar; llamar `ListarMetadataAsync()` vía scope corto.
- Renderizar `MudTable` con columnas: Tenant, ProveedorNombre, CuentaExternaId, BaseUrl, Badge IsActive, HintCredenciales, UpdatedAt, Acciones.
- Estado vacío con mensaje informativo cuando no hay registros.

**B. Formulario de alta/edición de metadata**
- Campos: selector de Tenant (MudSelect), ProveedorNombre, CuentaExternaId, BaseUrl, MudSwitch IsActive.
- En alta: también AccessToken y SigningSecret (Password + toggle).
- En edición: sin campos de credenciales; HintCredenciales visible como referencia (solo lectura).
- Validación de campos obligatorios con mensajes de error en campo.
- Al guardar: llamar `UpsertAsync` vía scope corto; snackbar de éxito; refrescar tabla.

**C. Toggle isActive desde tabla**
- `MudIconButton` o `MudSwitch` en la columna Badge → llamar `UpsertAsync` con el flag invertido (reutilizar credenciales cifradas ya almacenadas — pasar `null` o el ciphertext existente, según la firma de `UpsertAsync`).
- Snackbar de confirmación tras el cambio.

**D. Dialog Cambiar credenciales**
- `MudIconButton` en la fila → abrir `CambiarCredencialesDialog` con los parámetros correspondientes.
- Callback `OnSuccess` recarga la tabla para actualizar el hint.

**E. Eliminar**
- `MudIconButton` en la fila → `ConfirmDialog` estándar.
- Al confirmar: `EliminarAsync(id)` vía scope corto; quitar fila; snackbar.
- Al cancelar: sin cambios.

**F. Scope**
- Toda operación usa `IServiceScopeFactory.CreateScope()` con `await using`. Sin `DbContext` inyectado en el constructor ni a nivel de campo.

**Smoke:** navegar a `/proveedores`; verificar listado con hints enmascarados; crear, editar, cambiar credenciales, toggle isActive, eliminar; verificar que el acceso desde `/tenants?tenant=X` pre-filtra.

---

## Slice 9 — Cajas.razor (página principal)

**Requisito(s):** `ui-padron-cajas` completo → listado, filtro por tenant, revocar con advertencia, scope corto.

**Dependencias:** T-06 (`ListarPorTenantAsync`), T-08 (`RevocarAsync`).

**Modo TDD:** sin test automatizado; smoke manual.

### T-13 — [x] Cajas.razor (componente nuevo)

Archivo nuevo: `src/Dotar.Gateway/Dashboard/Components/Pages/Cajas.razor`.

Routing: `@page "/cajas"`.

**A. Filtro de tenant**
- `MudSelect` con lista de tenants (cargada via scope corto al init).
- Opción "Todos" (tenantId = null).
- Al cambiar selección → recargar tabla.

**B. Tabla de cajas**
- `MudTable` con `ServerData` (server-side pagination) sobre `ListarPorTenantAsync(tenantId, skip, take)`.
- Columnas: Identificador, CallbackUrl, UltimaVez, CreatedAt, Tenant (si no hay filtro), Acciones.
- Ordenar por UltimaVez descendente.
- Estado vacío con mensaje informativo.

**C. Revocar con advertencia de heartbeat**
- Al seleccionar "Revocar": computar si `UltimaVez >= UtcNow - TimeSpan.FromMinutes(cajaTtlMinutos)`.
  - El valor `cajaTtlMinutos` se lee de `IConfiguration["Seguridad:CajaTtlMinutos"]` (default 30 si no está configurado).
- Si dentro del TTL → `ConfirmDialog` con advertencia: "Esta caja tuvo actividad reciente (dentro de la ventana de TTL configurada) y podría volver a registrarse automáticamente en el próximo ciclo del ERP."
- Si fuera del TTL o `UltimaVez == null` → `ConfirmDialog` estándar sin advertencia.
- Al confirmar: `RevocarAsync(id)` vía scope corto; quitar fila; snackbar.
- Al cancelar: sin cambios.

**D. Scope**
- Toda operación usa `IServiceScopeFactory.CreateScope()` con `await using`.

**Smoke:** navegar a `/cajas`; verificar listado; filtrar por tenant; revocar una caja activa (verificar warning); revocar una caja inactiva (sin warning).

---

## Slice 10 — Regresión y limpieza

**Requisito(s):** todos los specs → "No modificar firmas ni comportamiento de métodos existentes"; "No afectar flujo de ingesta".

**Dependencias:** todos los slices anteriores completos.

**Modo TDD:** N/A (ejecución de suite existente).

### T-14 — [x] Regresión completa

- Ejecutar `dotnet test` con la suite completa.
- Verificar que NINGÚN test existente (ingesta 1-a-1, `IngestEndpoints`, Admin API, `RegistrarAsync`) falla.
- Hacer build de producción: `dotnet build src/Dotar.Gateway/Dotar.Gateway.csproj`.
- Smoke manual en dashboard: navegar por todas las páginas existentes (especialmente `/tenants`, `/monitor`, `/logs`) para confirmar que el menú y la navegación no se rompieron.

---

## Orden de ejecución (rama única)

```
T-01 → T-02 → T-03 → T-04 → T-05 → T-06 → T-07 → T-08
                                                       ↓
                                              T-09 → T-10 → T-11 → T-12 → T-13 → T-14
```

> T-09 (NavMenu) puede adelantarse al área UI en paralelo si hay dos pistas de trabajo, pero en rama única sigue después de T-08 para evitar que la página no exista al hacer smoke.

---

## Review Workload Forecast

| Ítem | Estimación |
|------|-----------|
| AppServices (2 archivos modificados) | ~120 líneas |
| CambiarCredencialesDialog.razor (nuevo) | ~90 líneas |
| Proveedores.razor (nuevo) | ~220 líneas |
| Cajas.razor (nuevo) | ~150 líneas |
| NavMenu.razor (modificado) | ~15 líneas |
| Tenants.razor (modificado) | ~10 líneas |
| Tests nuevos (2 archivos) | ~180 líneas |
| **Total estimado** | **~785 líneas** |

**Chained PRs recommended:** Sí — el total supera el budget de 400 líneas.

**Distribución sugerida (2 PRs):**

- **PR #1 — Backend + Tests** (Slices 1–4, T-01 a T-08): AppServices + tests xUnit. ~300 líneas. Mergeable de forma independiente; no afecta la UI existente.
- **PR #2 — UI + Navegación** (Slices 5–9, T-09 a T-13) + T-14 regresión: Componentes Razor, NavMenu, Tenants. ~485 líneas. Depende de PR #1 (usa los métodos nuevos de AppService).

**400-line budget risk:** Alto (total ~785 líneas).

**Decision needed before apply:** Sí — confirmar si se usa `stacked-to-main` o `feature-branch-chain`.

---

## Notas de implementación

- **Sin migración EF:** las entidades y DbSets ya existen en prod (v1.2.0). No crear ni ejecutar `dotnet ef migrations add`.
- **Sin paquetes NuGet nuevos:** todo reutiliza MudBlazor v9, EF Core, Polly y Microsoft.AspNetCore.DataProtection ya referenciados. bUnit es opcional y decisión del lead; NO agregarlo salvo aprobación explícita.
- **Umbral del warning de revocación:** leer siempre de `IConfiguration["Seguridad:CajaTtlMinutos"]` con default 30. El spec textual de `ui-padron-cajas` dice "10 minutos" — ese valor está desactualizado respecto a la decisión fijada (TTL configurable = 30 min).
- **Tests de componentes Razor:** sin infra bUnit en el repo → smoke manual en `/proveedores` y `/cajas`. Consistente con el resto del dashboard.
- **Idioma:** código, comentarios, UI y commits en español (convención del proyecto).
