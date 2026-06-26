# Tasks: proveedor-woocommerce-multisucursal

## Review Workload Forecast

| Campo | Valor |
|-------|-------|
| Líneas cambiadas estimadas | 600–700 (additions + deletions) |
| Riesgo presupuesto 400 líneas | Alto |
| PRs encadenados recomendados | Sí |
| División sugerida | PR 1 → Fundación + Extractor puro · PR 2 → Provider + Worker + Endpoint + Wiring |
| Delivery strategy | ask-on-risk |
| Chain strategy | pendiente (decisión requerida antes de apply) |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Work units sugeridos

| Unit | Objetivo | PR sugerido | Notas |
|------|----------|-------------|-------|
| WU-1 | Migración EF + propiedades Tenant + extractor puro (con todos sus tests) | PR 1 | Base: master. Aditivo puro, no toca código productivo compartido. |
| WU-2 | IWebhookProvider extendido + Provider WooCommerce + wiring Worker + Endpoint + Program.cs (con todos sus tests) | PR 2 | Base: PR 1 / feature branch. Toca código productivo compartido — requiere revisión priorizada. |

> **Nota**: IWebhookProvider, WebhookDispatcherWorker e IngestEndpoints son código productivo compartido con MercadoPago. Los tasks de WU-2 llevan la marca `[PRODUCTIVO]`.

---

## Fase 1 — Fundación de datos (WU-1, aditiva pura)

- [ ] **1.1** `[TEST-RED]` Escribir tests unitarios de `SucursalMetaDataExtractor` (8 casos) en `tests/Dotar.Gateway.Tests/Providers/SucursalMetaDataExtractorTests.cs`, referenciando la clase que todavía no existe. Verificar que el build falla con `CS0246`. Casos:
  - key presente, sin separador → value trim como routing key.
  - key presente, separador `"__"`, value `"003__extra"` → `"003"`.
  - key presente, value vacío → `Invalid`.
  - key ausente en el array → `Invalid`.
  - `meta_data` ausente o no-array → `Invalid`.
  - JSON inválido → `Invalid`.
  - value con guiones/underscore sin separador → se conserva entero.
  - separador configurado pero value sin separador → value completo.

- [ ] **1.2** `[TEST-GREEN]` Crear `src/Dotar.Gateway/Providers/SucursalMetaDataExtractor.cs` — clase estática pura `SucursalMetaDataExtractor` con método `public static RoutingKeyResult Extraer(string payload, string metaKey, string? separador)`. Sin I/O. Implementar hasta que los 8 tests de 1.1 pasen en verde.

- [ ] **1.3** Agregar las 4 propiedades nullable a la entidad `Tenant` en `src/Dotar.Gateway/Domain/Tenant.cs`:
  - `public bool RuteoProveedorActivo { get; set; }` (default `false`)
  - `public string? ProveedorRuteoNombre { get; set; }`
  - `public string? SucursalMetaKey { get; set; }`
  - `public string? SucursalMetaSeparador { get; set; }`

- [ ] **1.4** Generar la migración EF aditiva:
  ```
  dotnet ef migrations add AgregarRuteoProveedorWooCommerceMultiSucursal --project src/Dotar.Gateway/Dotar.Gateway.csproj
  ```
  Verificar que el archivo generado solo contiene `AddColumn` (no `DropColumn`, no `AlterColumn`, no recreación de tabla). Confirmar que `Down()` tiene los 4 `DropColumn` correspondientes.

*Commit WU-1: `feat(tenant): agregar campos de ruteo proveedor WooCommerce + extractor puro (TDD)`*

---

## Fase 2 — Contrato de provider (WU-2, toca código compartido)

- [ ] **2.1** `[PRODUCTIVO]` `[TEST-RED]` Escribir tests unitarios del provider en `tests/Dotar.Gateway.Tests/Providers/WooCommerceMultiSucursalProviderTests.cs` (4 casos):
  - `RutearSinEnriquecimiento(cualquier payload)` retorna `true`.
  - `RequiereConfigProveedor` retorna `false`.
  - `ExtraerRoutingKeyConConfig(payload, tenant)` delega en `SucursalMetaDataExtractor` con la key/separador del tenant.
  - `EnriquecerAsync(...)` retorna `Fallo("no soportado")` (contrato defensivo).
  Verificar que el build falla.

- [ ] **2.2** `[PRODUCTIVO]` Extender `IWebhookProvider` en `src/Dotar.Gateway/Providers/IWebhookProvider.cs` con dos miembros:
  - `bool RequiereConfigProveedor { get; }` (sin default — breaking para implementaciones existentes; agregar simultáneamente en MercadoPagoProvider).
  - `RoutingKeyResult ExtraerRoutingKeyConConfig(string payload, Tenant tenant)` con default method: `=> ExtraerRoutingKeyDesdeNotificacion(payload)` — MercadoPago hereda el default sin cambios.
  Agregar `bool RequiereConfigProveedor => true;` en `MercadoPagoProvider`.

- [ ] **2.3** `[TEST-GREEN]` Crear `src/Dotar.Gateway/Providers/WooCommerceMultiSucursalProvider.cs` implementando `IWebhookProvider`:
  - `Nombre => "woocommerce-multisucursal"`
  - `RequiereConfigProveedor => false`
  - `RutearSinEnriquecimiento(_) => true`
  - `ExtraerRoutingKeyConConfig(payload, tenant)` → llama `SucursalMetaDataExtractor.Extraer(payload, tenant.SucursalMetaKey!, tenant.SucursalMetaSeparador)`
  - `ExtraerRoutingKeyDesdeNotificacion(_) => RoutingKeyResult.Invalid` (no usar sin config).
  - `ResolverCuentaExterna(_, _) => null`
  - `ValidarFirmaEntrante(_, _, _) => false` (la firma WooCommerce la valida IngestEndpoints).
  - `EnriquecerAsync(_, _) => EnrichmentResult.Fallo("no soportado")`
  Verificar que los 4 tests de 2.1 pasan en verde.

- [ ] **2.4** `[PRODUCTIVO]` `[TEST-RED]` Escribir tests de integración del worker en `tests/Dotar.Gateway.Tests/Workers/WebhookDispatcherWorkerWooCommerceTests.cs` (5 casos usando `ProcesarWebhookParaTestAsync` + fakes existentes):
  - `ProveedorNombre="woocommerce-multisucursal"` + caja registrada vigente → forward a `CallbackUrl` con header `X-Caja-Signature` correcto.
  - sucursal no registrada → dead-letter `caja_no_encontrada` + `SystemLog` categoría `Worker` severidad `Error`.
  - sucursal vencida → dead-letter `caja_vencida` + `SystemLog` categoría `Worker` severidad `Error`.
  - `meta_data` ausente → dead-letter `sucursal_ausente` + `SystemLog` categoría `Worker` severidad `Error`.
  - **No regresión**: `ProveedorNombre=null` sigue flujo 1-a-1 intacto; `ProveedorNombre="mercadopago"` (`RequiereConfigProveedor=true`) sigue cargando `ProveedorWebhookConfig` y dead-letterea si falta (sin cambio de comportamiento).

- [ ] **2.5** `[PRODUCTIVO]` Modificar `WebhookDispatcherWorker.ProcesarFlujoProveedorAsync` en `src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs`:
  - Paso 2: envolver la carga/descifrado de `ProveedorWebhookConfig` en `if (provider.RequiereConfigProveedor)` — bloque existente sin cambios internos.
  - Rama `else`: cargar `Tenant` por `webhook.TenantId` con `AsNoTracking` para obtener `WebhookSecret`.
  - En el tramo de ruteo sin enriquecimiento: reemplazar `provider.ExtraerRoutingKeyDesdeNotificacion(payload)` por `provider.ExtraerRoutingKeyConConfig(payload, tenant)`.
  - En los 3 caminos no ruteables (routing key inválida, `NoEncontrada`, `Vencida`): agregar `_systemLog.Error(...)` con categoría `Worker` y `details` que incluya `proveedor`, `tenantId`, `identificador` (si aplica) y `motivo`.
  Verificar que los 5 tests de 2.4 pasan en verde.

- [ ] **2.6** `[PRODUCTIVO]` `[TEST-RED]` Escribir tests de integración de endpoint en `tests/Dotar.Gateway.Tests/Endpoints/IngestEndpointsWooCommerceTests.cs` (2 casos):
  - `POST /ingest/{slug}` con firma WooCommerce válida y `RuteoProveedorActivo=true` → webhook encolado con `ProveedorNombre = "woocommerce-multisucursal"` (o el valor de `ProveedorRuteoNombre` del tenant).
  - `POST /ingest/{slug}` con `RuteoProveedorActivo=false` → webhook encolado con `ProveedorNombre=null` (flujo 1-a-1, sin cambio).

- [ ] **2.7** `[PRODUCTIVO]` Modificar `IngestEndpoints.HandleIngest` en `src/Dotar.Gateway/Endpoints/IngestEndpoints.cs` (~3 líneas):
  ```csharp
  string? proveedorNombre = tenant.RuteoProveedorActivo ? tenant.ProveedorRuteoNombre : null;
  ```
  Asignar `ProveedorNombre = proveedorNombre` en el `QueuedWebhook` encolado. Verificar que los 2 tests de 2.6 pasan en verde.

- [ ] **2.8** Registrar el provider keyed en `src/Dotar.Gateway/Program.cs`:
  ```csharp
  builder.Services.AddKeyedSingleton<IWebhookProvider, WooCommerceMultiSucursalProvider>(
      "woocommerce-multisucursal",
      (sp, _) => new WooCommerceMultiSucursalProvider(
          sp.GetRequiredService<ILogger<WooCommerceMultiSucursalProvider>>()));
  ```
  Verificar que `dotnet build` pasa limpio.

- [ ] **2.9** Ejecutar la suite completa y confirmar verde:
  ```
  dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj
  ```
  Todos los tests nuevos (19 casos) y los tests pre-existentes deben pasar. Cero regresiones.

*Commit WU-2: `feat(woocommerce): provider multisucursal + wiring worker/endpoint/DI (TDD)`*

---

## Dependencias y orden de ejecución

```
1.1 → 1.2 → 1.3 → 1.4   (WU-1, secuencial, sin riesgo productivo)
                  ↓
2.1 → 2.2 → 2.3           (provider, secuencial)
            ↓
            2.4 → 2.5      (worker, secuencial)
            ↓
            2.6 → 2.7      (endpoint, secuencial)
                  ↓
                  2.8 → 2.9 (wiring + verificación final)
```

WU-1 puede ir en PR separado y se puede revisar/mergear de forma independiente.
WU-2 depende de WU-1 (necesita las propiedades de Tenant) y contiene todas las piezas que tocan código productivo compartido.
