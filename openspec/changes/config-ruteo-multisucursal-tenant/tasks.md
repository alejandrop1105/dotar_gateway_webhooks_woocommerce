# Tasks: Config Ruteo Multi-Sucursal Tenant

## Review Workload Forecast

| Campo | Valor |
|-------|-------|
| Líneas estimadas | ~320–360 (additions + deletions) |
| 400-line budget risk | Medium |
| Chained PRs recommended | No |
| Suggested split | Single PR con 4 work-unit commits |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending (no decision needed) |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: pending
400-line budget risk: Medium

### Suggested Work Units (commits dentro de un único PR)

| Unit | Goal | Archivos | Tests incluidos |
|------|------|----------|-----------------|
| WU-1 | Abstracción catálogo + registro DI | IProveedorRuteoCatalog.cs, ProveedorRuteoCatalog.cs, Program.cs | Sí — smoke test KeysValidas |
| WU-2 | Validación de ruteo en AppService | TenantAppService.cs (records + ValidarRuteo + Create/Update) | Sí — TenantAppServiceRuteoTests.cs (RED→GREEN) |
| WU-3 | Contrato API (requests + responses) | TenantApiEndpoints.cs | Sí — escenarios HTTP en TenantApiEndpointsTests.cs |
| WU-4 | Formulario Blazor | Tenants.razor | Manual (no hay tests Blazor en el proyecto) |

---

## Fase 1 — Fundación: Abstracción del catálogo de proveedores

- [x] 1.1 **[RED]** Crear `tests/Dotar.Gateway.Tests/Application/TenantAppServiceRuteoTests.cs` con un `FakeProveedorRuteoCatalog` (lista fija `["woocommerce-multisucursal", "mercadopago"]`) y el primer test fallando: `CreateAsync_ConRuteoActivo_SinProveedorNombre_Retorna400`.
- [x] 1.2 Crear `src/Dotar.Gateway/Application/IProveedorRuteoCatalog.cs` con la interfaz: `IReadOnlyCollection<string> KeysValidas { get; }`.
- [x] 1.3 Crear `src/Dotar.Gateway/Application/ProveedorRuteoCatalog.cs`: implementación que recibe `IEnumerable<string> keys` por constructor (lista explícita, evita problema de keyed DI sin resolución no-keyed); el factory en Program.cs pasa las keys hardcodeadas del keyed DI.
- [x] 1.4 Registrar en `src/Dotar.Gateway/Program.cs`: `AddSingleton<IProveedorRuteoCatalog>` con factory que retorna `new ProveedorRuteoCatalog(["mercadopago", "woocommerce-multisucursal"])`.
- [x] 1.5 **[Smoke test]** Agregar test `ProveedorRuteoCatalog_KeysValidas_ContieneLosProveedoresRegistrados` que verifica que `KeysValidas` contiene ambas keys.

---

## Fase 2 — Implementación central: Validación en TenantAppService

- [x] 2.1 Extender `CreateTenantInput` con 4 campos opcionales al final del record.
- [x] 2.2 Extender `UpdateTenantInput` con los mismos 4 campos opcionales.
- [x] 2.3 Agregar parámetro opcional `IProveedorRuteoCatalog? catalog = null` al constructor de `TenantAppService`.
- [x] 2.4 **[RED→GREEN]** Implementar `ValidarRuteo` privado.
- [x] 2.5 **[RED→GREEN]** En `CreateAsync`: campos de ruteo + ValidarRuteo + asignar al Tenant.
- [x] 2.6 **[RED→GREEN]** En `UpdateAsync`: update-parcial + apagado limpia dependientes + ValidarRuteo estado final.
- [x] 2.7 Verificar no-regresión: 5 archivos de test preexistentes pasan sin modificación (83 tests).

---

## Fase 3 — Integración API: Contrato HTTP público

> **MARCA DE CONTRATO PÚBLICO**: Los cambios en `TenantApiEndpoints.cs` afectan el contrato API consumido por el ERP. Priorizar revisión de los campos nuevos en responses Create/Update/Get y el nuevo 400 de ruteo.

- [x] 3.1 Extender `CreateTenantRequest` con los 4 campos opcionales al final.
- [x] 3.2 Extender `UpdateTenantRequest` con los mismos 4 campos opcionales.
- [x] 3.3 En `CreateTenant`: mapear + 4 campos en body 201 Created.
- [x] 3.4 En `UpdateTenant`: mapear + 4 campos en body 200 OK.
- [x] 3.5 En `GetTenantBySlug`: 4 campos en body 200 OK.
- [x] 3.6 **[RED→GREEN]** `TenantApiEndpointsRuteoTests.cs`: 9 tests HTTP. 28 tests originales en GREEN.

---

## Fase 4 — UI Blazor: Formulario `/tenants`

- [x] 4.1 Agregar al `TenantFormModel`: 4 campos de ruteo.
- [x] 4.2 Inyectar `IProveedorRuteoCatalog`; poblar `_proveedoresDisponibles` en `OnInitializedAsync`.
- [x] 4.3 Sección condicional: MudSwitch + MudSelect + MudTextField SucursalMetaKey (condicionales) + MudTextField SucursalMetaSeparador (siempre).
- [x] 4.4 `SaveTenant` (Create y Update): 4 campos mapeados; errores de AppService mostrados via `_errorMessage`.
- [x] 4.5 `LoadTenantForEdit`: 4 campos poblados desde Tenant.

---

## Orden de ejecución y paralelismo

```
WU-1 (Fases 1) → WU-2 (Fase 2) → WU-3 (Fase 3) → WU-4 (Fase 4)
```

---

## Checklist de no-regresión

- [x] `dotnet test` pasa en GREEN: 374/374 tests (352 preexistentes + 22 nuevos).
- [x] Los 5 archivos de test de `TenantAppService` (Create/Update/Delete/Toggle/Cache) pasan sin modificación.
- [x] `TenantSlugTests` sin cambios.
