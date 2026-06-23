# Proposal: UI de gestión de proveedores y padrón de cajas

## Intent

Tras deployar `ruteo-webhooks-multitenant` (v1.2.0 en producción) quedó pendiente la capa de UI Blazor. Hoy la config de proveedor solo se administra por API REST (`/api/proveedores/config`) y el padrón de cajas no tiene ninguna vista. Operar requiere curl/scripts, sin visibilidad del estado de las cajas auto-registradas. Este cambio cierra ese follow-up dando UI a ambos dominios dentro del dashboard existente.

## Scope

### In Scope
- Página `/proveedores` (+ `/proveedores/create`, `/proveedores/{Id:long}/edit`): listar, crear, editar metadata y borrar config de proveedor.
- Dialog separado "Cambiar credenciales" (re-ingreso de accessToken/signingSecret); preview enmascarado de últimos 4-6 chars (`••••••a3f2`) en listado/detalle.
- Página `/cajas`: listar padrón por tenant y borrar/revocar cajas, con advertencia si `UltimaVez` es reciente.
- `MudNavGroup` "Ruteo Multitenant" en NavMenu con links a ambas páginas.
- Acceso desde la fila del tenant en `/tenants` para agregar/ver config de proveedor de ese tenant.
- Extensión de `ProveedorWebhookConfigAppService` y `CajaRegistradaAppService` (listado + borrado + hint enmascarado).

### Out of Scope
- Cambios en el flujo de ingesta 1-a-1 ni en `IngestEndpoints`.
- Registro de cajas desde la UI (las cajas se auto-registran vía endpoint público del ERP).
- Exposición de credenciales completas al cliente.
- Nueva Admin API de cajas (la UI usa AppServices directos).

## Capabilities

> No existe `openspec/specs/` previo; ambas son capabilities nuevas a nivel UI.

### New Capabilities
- `ui-config-proveedor`: gestión visual de `ProveedorWebhookConfig` (metadata + credenciales separadas con preview enmascarado).
- `ui-padron-cajas`: visualización y borrado/revocación del padrón de `CajaRegistrada` por tenant.

### Modified Capabilities
- `proveedor-webhook-config-service`: agregar listado de metadata con hint de credenciales enmascarado (sin valores en claro) y borrado.
- `caja-registrada-service`: agregar listado por tenant y borrado/revocación. No tocar `RegistrarAsync` ni la validación anti-SSRF existente.

## Approach

- **Acceso a datos**: AppServices directos vía `IServiceScopeFactory` (scope corto por operación), siguiendo el patrón de `Tenants.razor`. NO consumir la Admin API REST desde la UI para no exponer la API Key en el bundle.
- **Credenciales**: el form de proveedor edita solo metadata (tenant, proveedorNombre, cuentaExternaId, baseUrl, isActive). Un dialog "Cambiar credenciales" acepta accessToken/signingSecret. El secret completo nunca sale del servidor; se descifra en el SERVIDOR y el DTO de listado/detalle solo expone el hint enmascarado de los últimos 4-6 chars.
- **Cajas**: solo lectura + borrado. Columnas: tenant, identificador, callbackUrl, `UltimaVez`. Advertencia al borrar si el heartbeat es reciente (la caja reaparece en el próximo auto-registro del ERP).
- **Patrón**: routing multi-`@page`, form model interno, `ConfirmDialog.razor`, `ISnackbar`, MudBlazor v9. Nada de DbContext circuit-scoped en componentes.

## Affected Areas

| Area | Impacto | Descripción |
|------|---------|-------------|
| `Dashboard/Components/Pages/Proveedores.razor` | Nuevo | Página de config de proveedor |
| `Dashboard/Components/Pages/Cajas.razor` | Nuevo | Página del padrón de cajas |
| `Dashboard/Components/Layout/NavMenu.razor` | Modificado | `MudNavGroup` "Ruteo Multitenant" |
| `Dashboard/Components/Pages/Tenants.razor` | Modificado | Acción fila → config de proveedor |
| `Application/ProveedorWebhookConfigAppService.cs` | Modificado | Listado metadata + hint enmascarado + borrado |
| `Application/CajaRegistradaAppService.cs` | Modificado | Listado por tenant + borrado |

## Risks

| Riesgo | Probabilidad | Mitigación |
|--------|-------------|------------|
| Fuga de credenciales en claro al cliente | Media | Solo hint enmascarado en DTO; descifrado server-side; nunca el secret completo |
| Romper ruteo en producción al tocar AppServices | Baja | Solo agregar métodos nuevos; no modificar firmas ni `RegistrarAsync` |
| Anti-patrón DbContext circuit-scoped | Baja | `ScopeFactory.CreateScope()` por operación, scope corto |
| Performance del listado de cajas | Media | Paginación / `.Take(100)` por tenant |

## Rollback Plan

Revertir el commit/PR: las páginas y los métodos nuevos de AppService se eliminan sin afectar el flujo de ingesta ni la Admin API existente. No hay migraciones EF Core (las entidades ya existen). La config de proveedor sigue gestionándose por API REST como hasta hoy.

## Dependencies

- `ruteo-webhooks-multitenant` (v1.2.0) ya en producción: entidades `ProveedorWebhookConfig` y `CajaRegistrada`, DbSets y AppServices ya registrados.
- `IDataProtector` para descifrar y derivar el hint enmascarado server-side.

## Success Criteria

- [ ] `/proveedores` lista, crea, edita metadata y borra config; el dialog "Cambiar credenciales" funciona y solo muestra hint enmascarado.
- [ ] `/cajas` lista por tenant y borra/revoca, con advertencia si `UltimaVez` es reciente.
- [ ] Ningún secret completo sale del servidor (verificable en payload de red).
- [ ] NavMenu muestra "Ruteo Multitenant" y la fila de tenant da acceso a su config.
- [ ] El flujo de ingesta 1-a-1 y la Admin API siguen intactos.
