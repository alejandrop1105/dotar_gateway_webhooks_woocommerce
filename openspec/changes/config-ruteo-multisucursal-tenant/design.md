# Design: Exponer configuración de ruteo multi-sucursal del Tenant (UI + API)

Exponer por formulario Blazor y API REST los 4 campos de ruteo multi-sucursal que ya existen en la entidad `Tenant` (`RuteoProveedorActivo`, `ProveedorRuteoNombre`, `SucursalMetaKey`, `SucursalMetaSeparador`). La lógica de ruteo ya está implementada; este change solo agrega la capa de administración (validación compartida + persistencia) sin romper el contrato actual de la API que consume el ERP.

## Quick path (qué se construye)

1. Extender `CreateTenantInput` / `UpdateTenantInput` (Application) y `CreateTenantRequest` / `UpdateTenantRequest` (HTTP) con los 4 campos, todos opcionales con default.
2. Centralizar la validación de negocio en `TenantAppService` (un único método privado usado por `CreateAsync` y `UpdateAsync`).
3. Validar `ProveedorRuteoNombre` contra las keys reales del keyed DI, resueltas desde `IWebhookProvider.Nombre` (sin strings hardcodeados).
4. Extender el form `/tenants` (Blazor) con los 4 campos y validación condicional, reusando la validación del AppService (sin duplicar reglas de negocio).
5. Incluir los nuevos campos en los bodies de respuesta Create/Update/Get de la API.
6. Tests: unit de validación en `TenantAppService`, integración de endpoints con y sin campos, y test de compatibilidad hacia atrás.

Sin migración EF (las columnas ya existen desde la migración `20260626141012_AgregarRuteoProveedorWooCommerceMultiSucursal`).

## Arquitectura — visión general

```
┌─────────────────┐     ┌──────────────────┐
│ Tenants.razor   │     │ TenantApiEndpoints│
│ (Blazor form)   │     │ (Minimal API)     │
│ TenantFormModel │     │ Create/UpdateReq  │
└────────┬────────┘     └─────────┬─────────┘
         │ Create/UpdateInput     │ Create/UpdateInput
         └───────────┬────────────┘
                     ▼
        ┌────────────────────────────┐
        │ TenantAppService           │
        │  - CreateAsync             │
        │  - UpdateAsync             │
        │  - ValidarRuteo(...)  ◄────┼── regla única compartida
        └─────────────┬──────────────┘
                      │ keys válidas
                      ▼
        ┌────────────────────────────┐
        │ IProveedorRuteoCatalog     │  ← abstracción nueva
        │  KeysValidas: IReadOnly... │     (impl lee IWebhookProvider.Nombre)
        └────────────────────────────┘
```

La validación de negocio vive **una sola vez** en `TenantAppService`. UI y API la respetan porque ambas delegan en el mismo servicio. Ningún componente reimplementa reglas de ruteo.

## Decisiones (ADR)

### ADR-1 — Validar `ProveedorRuteoNombre` contra el keyed DI vía abstracción `IProveedorRuteoCatalog`

**Decisión.** Crear una abstracción `IProveedorRuteoCatalog` con una propiedad `IReadOnlyCollection<string> KeysValidas`. Su implementación de producción (`ProveedorRuteoCatalog`) recibe `IEnumerable<IWebhookProvider>` por DI y expone `provider.Nombre` como conjunto de keys válidas. `TenantAppService` depende de `IProveedorRuteoCatalog`, no de `IEnumerable<IWebhookProvider>` directo.

**Por qué.** Las keys canónicas son exactamente los `IWebhookProvider.Nombre` registrados en `Program.cs` (`mercadopago`, `woocommerce-multisucursal`). Resolver la lista desde los providers reales garantiza que **nunca se desincronice** del keyed DI: si mañana se registra un nuevo provider, la validación lo acepta sin tocar `TenantAppService`. La key de DI y `Nombre` ya están acopladas por contrato (ver `IWebhookProvider.cs:37` y `Program.cs:102/114`).

**Por qué la abstracción y no inyectar `IEnumerable<IWebhookProvider>` directo.**
1. **No romper 5 archivos de test.** Hoy `TenantAppService` se instancia con 3 parámetros (`new TenantAppService(_db, _cache, NullLogger...)`) en `TenantAppServiceCreateTests`, `UpdateTests`, `DeleteTests`, `ToggleTests`, `CacheTests`. Una dependencia chica y fácil de fakear (`FakeProveedorRuteoCatalog` con una lista fija) mantiene los tests legibles y aislados de los providers reales (que arrastran `HttpClient`, loggers, etc.).
2. **Inversión de dependencias correcta.** `TenantAppService` no necesita conocer `IWebhookProvider` (concepto de la capa de ingesta/forwarding); solo necesita "¿es esta key un proveedor de ruteo válido?". La abstracción expresa exactamente esa necesidad.
3. **Testeo unitario sin DI ni HTTP.** Cumple el requisito de strict TDD: la validación se testea con un catálogo fake en memoria.

**Compatibilidad de constructor.** El nuevo parámetro `IProveedorRuteoCatalog` se agrega como **último parámetro opcional con default `null`**; si es null, `TenantAppService` usa un catálogo vacío internamente (`EmptyCatalog`). Así los 5 tests existentes compilan sin cambios y solo los tests nuevos de ruteo inyectan el fake. En `Program.cs` se registra la implementación real, por lo que en runtime nunca es null.

**Registro DI (Program.cs).** `builder.Services.AddScoped<IProveedorRuteoCatalog, ProveedorRuteoCatalog>();`. La implementación recibe `IEnumerable<IWebhookProvider>`, que ASP.NET resuelve a partir de los `AddKeyedSingleton<IWebhookProvider, ...>` existentes (los registros keyed también satisfacen la resolución no-keyed del tipo de servicio).

**Alternativas descartadas.**
- *Lista de strings hardcodeada* (`{"mercadopago","woocommerce-multisucursal"}`): se desincroniza del DI al primer provider nuevo. Es el anti-patrón que el proposal pide evitar.
- *Inyectar `IEnumerable<IWebhookProvider>` directo en `TenantAppService`*: rompe la firma probada, acopla la capa Application al concepto de provider y obliga a fabricar providers reales (con HttpClient) en los tests.
- *Leer las keys del `IServiceProvider` por reflexión de keyed services*: frágil y no hay API pública estable para enumerar keys.

### ADR-2 — Regla de validación única en `TenantAppService.ValidarRuteo(...)`

**Decisión.** Un método privado estático/instancia `ValidarRuteo(bool? activo, string? proveedor, string? metaKey)` (o sobre los campos resueltos) que devuelve `Result<...>.Validation(...)` ante error y se invoca tanto en `CreateAsync` como en `UpdateAsync` sobre el **estado final efectivo** del tenant (no sobre el input crudo). Reusa el patrón `Result<Tenant>.Validation(mensaje)` y `ResultError.Validation` ya existente.

**Por qué validar sobre el estado efectivo y no sobre el input.** En `UpdateAsync` los campos llegan parciales: hay que validar combinando lo que viene en el input con lo que ya tiene el tenant persistido. Ejemplo: un update que solo manda `SucursalMetaKey: ""` sobre un tenant con `RuteoProveedorActivo=true` debe rechazarse. Validar el input aislado no captura ese caso; validar el estado final sí.

**Forma concreta de las reglas (proposal Business Rules v1):**
- Si el estado efectivo tiene `RuteoProveedorActivo == true`:
  - `ProveedorRuteoNombre` no puede ser null/blank → `Validation("Con ruteo por proveedor activo, 'proveedorRuteoNombre' es obligatorio.")`
  - `ProveedorRuteoNombre` debe estar en `IProveedorRuteoCatalog.KeysValidas` → `Validation("El proveedor de ruteo '{x}' no está registrado. Válidos: {lista}.")`
  - `SucursalMetaKey` no puede ser null/blank → `Validation("Con ruteo por proveedor activo, 'sucursalMetaKey' es obligatorio.")`
  - `SucursalMetaSeparador` siempre opcional (sin validación de obligatoriedad).
- Si el estado efectivo tiene `RuteoProveedorActivo == false`: no se valida proveedor/metaKey/separador (se limpian, ver ADR-4).

**Por qué en `TenantAppService` y no en la entidad o un validador externo.** El AppService ya es el punto único donde convergen UI y API (proposal y código actual lo confirman: el form Blazor y los endpoints delegan ambos en `CreateAsync`/`UpdateAsync`). Mantener la regla ahí evita duplicación y mantiene el mensaje de error consistente entre los dos canales.

**Alternativas descartadas.**
- *Validar en la entidad `Tenant`*: la entidad no conoce `IProveedorRuteoCatalog` ni debería; mezclaría dominio con catálogo de infraestructura.
- *FluentValidation / validador por DTO*: agregaría dependencia y dejaría la validación en la capa HTTP, sin cubrir el canal Blazor con la misma regla.

### ADR-3 — Extensión de inputs/DTOs: 4 campos opcionales con default

**Decisión.** Agregar los 4 campos al final de cada record, todos nullable con default, para preservar la compatibilidad binaria y de llamada por parámetros nombrados/posicionales.

| Record | Campos agregados (al final) |
|--------|-----------------------------|
| `CreateTenantInput` | `bool? RuteoProveedorActivo = null`, `string? ProveedorRuteoNombre = null`, `string? SucursalMetaKey = null`, `string? SucursalMetaSeparador = null` |
| `UpdateTenantInput` | idem (las 4 nullable con default null) |
| `CreateTenantRequest` | idem |
| `UpdateTenantRequest` | idem |

**Por qué `bool?` y no `bool` para `RuteoProveedorActivo`.** En Create un `bool?` ausente se resuelve a `false` (default de negocio). En Update, `bool?` distingue "no tocar" (null) de "apagar" (false) — igual que `IsActive` ya hace hoy. Un `bool` no-nullable no podría expresar "no tocar" en update parcial.

**Por qué al final con default.** El ERP llama la API con bodies JSON; los campos ausentes deserializan a null y no rompen (riesgo High del proposal mitigado). Los call-sites internos por parámetros nombrados (endpoints, form) siguen compilando sin cambios obligatorios.

### ADR-4 — Semántica de update parcial y limpieza, coherente con las convenciones existentes

**Decisión.** Aplicar el patrón ya documentado en `UpdateTenant` (`""` limpia strings opcionales, `null` = sin cambio) extendido a estos campos, con una regla explícita de apagado que limpia dependientes.

| Campo | `null` (Update) | valor provisto | Limpieza |
|-------|-----------------|----------------|----------|
| `RuteoProveedorActivo` | sin cambio | aplica `true`/`false` | `false` ⇒ apaga ruteo |
| `ProveedorRuteoNombre` | sin cambio | aplica trim | `""` ⇒ null |
| `SucursalMetaKey` | sin cambio | aplica trim | `""` ⇒ null |
| `SucursalMetaSeparador` | sin cambio | aplica trim | `""` ⇒ null |

**Regla de apagado (crítica).** Cuando el estado efectivo queda `RuteoProveedorActivo == false`, `UpdateAsync`/`CreateAsync` **fuerzan** `ProveedorRuteoNombre = null`, `SucursalMetaKey = null`, `SucursalMetaSeparador = null` antes de persistir. Esto evita dejar configuración huérfana (un proveedor seteado con ruteo apagado) y hace el apagado idempotente: basta mandar `RuteoProveedorActivo: false` para desactivar y limpiar todo.

**Orden de operaciones en `UpdateAsync`:** (1) aplicar campos provistos sobre el tenant; (2) calcular estado efectivo; (3) si activo → `ValidarRuteo` sobre estado efectivo (corta con error si falla); (4) si inactivo → limpiar dependientes; (5) `SaveChanges` + invalidar caché.

**En `CreateAsync`:** `RuteoProveedorActivo` ausente ⇒ `false` ⇒ los 3 dependientes quedan null aunque hayan llegado valores (apagado gana). Si llega `true`, se valida igual que en update.

**Por qué esta semántica.** Es la extensión literal de la convención existente (`signatureHeader: ""` → default; `retryPolicyId: 0` → desasociar). El `string vacío = limpiar` ya está en el código para `SignatureHeader`; reusarlo evita inventar una convención nueva.

**Alternativa descartada.** *Permitir apagar sin limpiar dependientes*: deja datos inconsistentes y obliga al consumidor a limpiar a mano. Rechazado por el edge case explícito del proposal ("Update parcial `RuteoProveedorActivo=false` → desactivar+limpiar").

### ADR-5 — Form Blazor: campos condicionales que reusan la validación del AppService

**Decisión.** Agregar a `TenantFormModel` (clase privada del componente) 4 propiedades:
`bool RuteoProveedorActivo`, `string ProveedorRuteoNombre`, `string SucursalMetaKey`, `string SucursalMetaSeparador`. Renderizar:
- Un `MudSwitch` para `RuteoProveedorActivo`.
- Cuando está activo (`@if (_form.RuteoProveedorActivo)`), mostrar: un `MudSelect<string>` para `ProveedorRuteoNombre` poblado desde `IProveedorRuteoCatalog.KeysValidas` (inyectado vía scope, igual que ya se resuelve `TenantAppService`), un `MudTextField` para `SucursalMetaKey` y uno para `SucursalMetaSeparador`.
- Cuando está inactivo, ocultar los 3 (y no enviarlos como obligatorios).

**No duplicar la validación de negocio.** El componente **no** reimplementa la regla "key requerida si activo" como lógica propia de negocio; solo hace *UX hints* (deshabilitar/ocultar). La validación dura sigue siendo la del `TenantAppService`: el form arma el `CreateTenantInput`/`UpdateTenantInput` con los 4 campos y, si `CreateAsync`/`UpdateAsync` devuelve `Validation`, muestra `result.Message` en el `MudAlert` de error existente (`_errorMessage`). Así el mensaje es idéntico al de la API.

**Mapeo form → input.**
- Create: `RuteoProveedorActivo = _form.RuteoProveedorActivo`; los 3 strings se mandan tal cual (el AppService limpia si está inactivo).
- Update: `RuteoProveedorActivo = _form.RuteoProveedorActivo`; strings tal cual. Al cargar para editar (`LoadTenantForEdit`), poblar el form desde el tenant (`ProveedorRuteoNombre ?? ""`, etc.).

**Poblar el `MudSelect` de proveedores.** Resolver `IProveedorRuteoCatalog` en `OnInitializedAsync`/`LoadTenants` (scope corto, como el resto) y guardar `KeysValidas` en un campo `_proveedoresRuteo` para el `@foreach`. Evita hardcodear las opciones en el markup.

**Por qué `MudSelect` y no `MudTextField` para el proveedor.** Las keys válidas son un conjunto cerrado conocido en runtime; un select previene typos y refleja exactamente lo que la API aceptará, mejorando el feedback antes de enviar.

**Alternativa descartada.** *Validar en el componente con lógica propia*: duplica la regla, diverge del mensaje de la API y viola el principio de fuente única que el proposal exige.

### ADR-6 — Respuesta de la API: incluir los 4 campos en Create/Update/Get

**Decisión.** Agregar al body de respuesta de `CreateTenant`, `UpdateTenant` y `GetTenantBySlug` los 4 campos en camelCase, coherente con el resto del payload:
`ruteoProveedorActivo`, `proveedorRuteoNombre`, `sucursalMetaKey`, `sucursalMetaSeparador`.

**Por qué.** Quien crea/edita por API debe poder confirmar el estado persistido (incluida la limpieza por apagado) sin un GET extra. Es aditivo: agregar propiedades al objeto anónimo de respuesta no rompe consumidores existentes (que ignoran campos desconocidos).

**Nota.** El body de Create hoy incluye `webhookSecret`; los nuevos campos no son secretos, se exponen tal cual. `GetTenantBySlug` hoy no expone `webhookSecret` (correcto) y se le agregan solo los 4 de ruteo.

## Archivos a tocar

| Archivo | Cambio |
|---------|--------|
| `src/Dotar.Gateway/Application/TenantAppService.cs` | +4 campos en `CreateTenantInput` y `UpdateTenantInput`; nuevo parámetro opcional `IProveedorRuteoCatalog? = null` en el constructor con fallback a catálogo vacío; método privado `ValidarRuteo(...)`; lógica de aplicar/limpiar/validar en `CreateAsync` y `UpdateAsync`. |
| `src/Dotar.Gateway/Application/IProveedorRuteoCatalog.cs` (nuevo) | Interfaz `IProveedorRuteoCatalog { IReadOnlyCollection<string> KeysValidas { get; } }`. |
| `src/Dotar.Gateway/Application/ProveedorRuteoCatalog.cs` (nuevo) | Implementación que recibe `IEnumerable<IWebhookProvider>` y proyecta `.Nombre`. |
| `src/Dotar.Gateway/Endpoints/TenantApiEndpoints.cs` | +4 campos en `CreateTenantRequest` y `UpdateTenantRequest`; mapeo a los inputs; +4 campos en los bodies de respuesta Create/Update/Get; actualizar el `<summary>` de convenciones de update. |
| `src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor` | +4 props en `TenantFormModel`; UI condicional (switch + select + 2 textfields); resolución de `IProveedorRuteoCatalog`; mapeo form↔input; poblar form en `LoadTenantForEdit`. |
| `src/Dotar.Gateway/Program.cs` | `AddScoped<IProveedorRuteoCatalog, ProveedorRuteoCatalog>();` (no se tocan los registros keyed). |

**Sin migración EF.** Las 4 columnas ya existen en la entidad y en la migración aplicada. Confirmado: no se requiere `dotnet ef migrations add`.

## Estrategia de testing (strict TDD)

Test command: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`. La validación es 100% unit-testeable en `TenantAppService` con un `FakeProveedorRuteoCatalog` (lista fija), sin levantar UI ni HTTP.

**Unit — `TenantAppService` (Create + Update) con `FakeProveedorRuteoCatalog`:**
- [ ] Create sin campos de ruteo → tenant con `RuteoProveedorActivo=false` y dependientes null (compat).
- [ ] Create con `RuteoProveedorActivo=true` + proveedor válido + metaKey → success, persiste los 4.
- [ ] Create con `RuteoProveedorActivo=true` sin `ProveedorRuteoNombre` → `Validation`.
- [ ] Create con `RuteoProveedorActivo=true` sin `SucursalMetaKey` → `Validation`.
- [ ] Create con `ProveedorRuteoNombre` no registrado → `Validation` (mensaje incluye lista de válidos).
- [ ] Create con `RuteoProveedorActivo=false` pero proveedor/metaKey provistos → se limpian a null (apagado gana).
- [ ] Update parcial sin campos de ruteo sobre tenant con ruteo activo → no cambia ruteo.
- [ ] Update `RuteoProveedorActivo=false` → desactiva y limpia los 3 dependientes.
- [ ] Update que deja estado efectivo activo pero `SucursalMetaKey=""` → `Validation` (valida estado final, no input).
- [ ] Update con `ProveedorRuteoNombre` inválido sobre tenant activo → `Validation`.
- [ ] Constructor con `IProveedorRuteoCatalog` null (fallback) → catálogo vacío; activar ruteo con cualquier proveedor → `Validation` (ninguno válido).

**Integración — `TenantApiEndpointsTests` (Create/Update):**
- [ ] POST Create con body SIN los 4 campos → 201 (compat hacia atrás del contrato del ERP).
- [ ] POST Create con ruteo activo + proveedor válido + metaKey → 201 y body incluye los 4 campos.
- [ ] POST Create con ruteo activo sin proveedor → 400 con mensaje claro.
- [ ] PUT Update apagando ruteo → 200 y body refleja dependientes en null.
- [ ] PUT Update con proveedor inválido → 400.
- [ ] GET by slug → body incluye los 4 campos.

**Compatibilidad:** los 5 tests existentes de `TenantAppService` deben seguir compilando y pasando sin cambios (gracias al parámetro de constructor opcional). Verificar que no se tocó su `new TenantAppService(...)` de 3 args.

**Form Blazor:** sin tests de UI (fuera de scope; el proyecto no testea componentes Blazor). La cobertura de la regla está en el AppService, que es lo que el form invoca.

## Riesgos residuales

- **Resolución `IEnumerable<IWebhookProvider>` con registros keyed.** En .NET 9, los `AddKeyedSingleton<IWebhookProvider, ...>` SÍ se resuelven al pedir `IEnumerable<IWebhookProvider>` no-keyed. Riesgo bajo, pero debe confirmarse en apply con un test de smoke (o un test de integración que verifique que `KeysValidas` contiene `mercadopago` y `woocommerce-multisucursal`). Si fallara, alternativa: registrar también un `AddSingleton` no-keyed por provider o construir el catálogo desde una lista explícita en `Program.cs`.
- **Mensaje de error con lista de válidos.** Exponer las keys en el mensaje es útil para el operador pero filtra nombres internos de providers por la API; aceptable porque no son secretos.
- **Edición de tenant productivo.** Tenants productivos (`panificadora-mauri`, `add-distribuidora`) no deben tocarse sin permiso; el form ahora permite editar ruteo — es UX esperada, pero el operador debe tener cuidado. No es un riesgo de código.
- **Doble fuente de "modo activo" en el form.** El switch controla visibilidad; si el usuario activa, completa y luego desactiva sin guardar, los campos quedan en el modelo pero el AppService los limpia al persistir. Coherente, pero conviene limpiar el modelo en el toggle para que la UX no muestre datos que se van a descartar (decisión menor, delegada a tasks/apply).
