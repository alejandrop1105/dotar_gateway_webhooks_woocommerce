# Delta para tenant-management

## Dominio
Administración de tenants (UI + API). Este delta modifica el comportamiento de `CreateAsync` y `UpdateAsync` en `TenantAppService`, los DTOs de `TenantApiEndpoints`, y el formulario `/tenants`.

## NON-GOALS (fuera de spec)
- Lógica de ruteo y extracción de `shipping_lines` (ya implementado en PR #11/12).
- Gestión de sucursales vía `/registro-caja`.
- Auth nueva para la API.

---

## ADDED Requirements

### Requirement: Campos de ruteo opcionales en Create

El sistema DEBE aceptar los campos `RuteoProveedorActivo`, `ProveedorRuteoNombre`, `SucursalMetaKey` y `SucursalMetaSeparador` como opcionales en `CreateTenantRequest` y `CreateTenantInput`. Si no se proveen, DEBEN aplicarse los siguientes defaults: `RuteoProveedorActivo = false`, `ProveedorRuteoNombre = ""`, `SucursalMetaKey = ""`, `SucursalMetaSeparador = ""`.

#### Scenario: Create con campos de ruteo provistos

- GIVEN una solicitud `POST /api/tenants` con `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="woocommerce-multisucursal"`, `SucursalMetaKey="_woosea_item_id"` y `SucursalMetaSeparador=":"`
- WHEN `TenantAppService.CreateAsync` procesa la solicitud
- THEN el tenant se persiste con los 4 campos según los valores recibidos
- AND la respuesta incluye los 4 campos en el cuerpo `201 Created`

#### Scenario: Create sin campos de ruteo — compatibilidad hacia atrás (no-regresión)

- GIVEN una solicitud `POST /api/tenants` que NO incluye ninguno de los 4 campos de ruteo (payload idéntico al que el ERP envía hoy)
- WHEN `TenantAppService.CreateAsync` procesa la solicitud
- THEN el tenant se crea exitosamente con `RuteoProveedorActivo=false` y los 3 campos de texto vacíos
- AND la respuesta es `201 Created` sin error, idéntica al comportamiento previo al change

---

### Requirement: Campos de ruteo opcionales en Update parcial

El sistema DEBE aceptar los campos de ruteo como opcionales en `UpdateTenantRequest` y `UpdateTenantInput`. Un campo ausente (null) DEBE dejarse sin cambio. Un campo presente (incluido string vacío explícito) DEBE aplicarse.

#### Scenario: Update que modifica solo los campos de ruteo

- GIVEN un tenant existente con `RuteoProveedorActivo=false`
- WHEN `PUT /api/tenants/{slug}` recibe `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="woocommerce-multisucursal"` y `SucursalMetaKey="_woosea_item_id"` (sin `SucursalMetaSeparador`)
- THEN los 3 campos provistos se actualizan, `SucursalMetaSeparador` permanece sin cambio
- AND la respuesta incluye el estado completo de los 4 campos

#### Scenario: Update que no incluye campos de ruteo

- GIVEN un tenant existente con `RuteoProveedorActivo=true`
- WHEN `PUT /api/tenants/{slug}` recibe solo `Name` y `TargetUrl` (sin campos de ruteo)
- THEN `RuteoProveedorActivo` y los campos dependientes permanecen sin cambio

---

### Requirement: Validación compartida — activar ruteo sin campos obligatorios

El sistema DEBE rechazar, mediante una única regla en `TenantAppService`, cualquier solicitud que establezca `RuteoProveedorActivo=true` sin proveer `ProveedorRuteoNombre` o sin proveer `SucursalMetaKey`. Esta regla DEBE aplicar tanto en `CreateAsync` como en `UpdateAsync`. La respuesta HTTP DEBE ser `400 Bad Request` con un mensaje que identifique el campo faltante.

#### Scenario: Create con ruteo activo sin ProveedorRuteoNombre

- GIVEN una solicitud `POST /api/tenants` con `RuteoProveedorActivo=true`, `SucursalMetaKey="_woosea_item_id"` y sin `ProveedorRuteoNombre`
- WHEN `CreateAsync` evalúa la solicitud
- THEN retorna `Result` con `IsSuccess=false` y error que indica que `ProveedorRuteoNombre` es obligatorio cuando el ruteo está activo
- AND la API responde `400 Bad Request`; el tenant NO se persiste

#### Scenario: Create con ruteo activo sin SucursalMetaKey

- GIVEN una solicitud `POST /api/tenants` con `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="woocommerce-multisucursal"` y sin `SucursalMetaKey`
- WHEN `CreateAsync` evalúa la solicitud
- THEN retorna `Result` con `IsSuccess=false` y error que indica que `SucursalMetaKey` es obligatoria cuando el ruteo está activo
- AND la API responde `400 Bad Request`; el tenant NO se persiste

#### Scenario: Update con ruteo activo sin campos obligatorios

- GIVEN un tenant existente con `RuteoProveedorActivo=false`
- WHEN `PUT /api/tenants/{slug}` recibe `RuteoProveedorActivo=true` y `ProveedorRuteoNombre` ausente (null)
- THEN `UpdateAsync` retorna `Result` con `IsSuccess=false` y error de campo faltante
- AND la API responde `400 Bad Request`; los valores previos del tenant no cambian

---

### Requirement: Validación compartida — ProveedorRuteoNombre inválido

El sistema DEBE rechazar cualquier valor de `ProveedorRuteoNombre` que no corresponda a un provider registrado en el keyed DI. Los valores válidos son las keys registradas (p.ej. `"mercadopago"`, `"woocommerce-multisucursal"`). Esta validación DEBE aplicar en `CreateAsync` y en `UpdateAsync`. La respuesta HTTP DEBE ser `400 Bad Request`.

#### Scenario: ProveedorRuteoNombre con valor desconocido en Create

- GIVEN una solicitud `POST /api/tenants` con `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="paypal"` y `SucursalMetaKey="_id"`
- WHEN `CreateAsync` evalúa la solicitud
- THEN retorna `Result` con `IsSuccess=false` y error que indica que `"paypal"` no es un proveedor registrado
- AND la API responde `400 Bad Request`; el tenant NO se persiste

#### Scenario: ProveedorRuteoNombre con valor desconocido en Update

- GIVEN un tenant existente
- WHEN `PUT /api/tenants/{slug}` recibe `ProveedorRuteoNombre="proveedor-inexistente"`
- THEN `UpdateAsync` retorna `Result` con `IsSuccess=false` y error de proveedor inválido
- AND la API responde `400 Bad Request`

---

### Requirement: Semántica de apagado — desactivar y limpiar ruteo

El sistema DEBE implementar la siguiente semántica cuando se recibe `RuteoProveedorActivo=false` de forma explícita: los campos dependientes (`ProveedorRuteoNombre`, `SucursalMetaKey`, `SucursalMetaSeparador`) DEBEN limpiarse a string vacío, coherente con la convención de apagado de `SignatureScheme=None` (que vacía `WebhookSecret`). Esta limpieza DEBE ocurrir en `CreateAsync` (no aplica, por defaults) y en `UpdateAsync`.

#### Scenario: Update que desactiva el ruteo explícitamente

- GIVEN un tenant existente con `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="woocommerce-multisucursal"`, `SucursalMetaKey="_id"` y `SucursalMetaSeparador=":"`
- WHEN `PUT /api/tenants/{slug}` recibe `RuteoProveedorActivo=false`
- THEN `RuteoProveedorActivo` pasa a `false` y los 3 campos dependientes se vacían a string vacío
- AND la respuesta refleja los 4 campos con sus nuevos valores

#### Scenario: Update con RuteoProveedorActivo=false y campos dependientes omitidos (null)

- GIVEN un tenant existente con ruteo activo
- WHEN `PUT /api/tenants/{slug}` recibe solo `RuteoProveedorActivo=false` (los demás campos de ruteo son null/omitidos)
- THEN el apagado limpia automáticamente los 3 campos dependientes sin requerir que el caller los envíe explícitamente

---

### Requirement: Respuesta de API incluye campos de ruteo

El sistema DEBE incluir los 4 campos de ruteo en el cuerpo de respuesta de `POST /api/tenants` (201 Created), `PUT /api/tenants/{slug}` (200 OK) y `GET /api/tenants/{slug}` (200 OK).

#### Scenario: GET de tenant con ruteo activo

- GIVEN un tenant con `RuteoProveedorActivo=true` y campos de ruteo configurados
- WHEN `GET /api/tenants/{slug}` responde
- THEN el cuerpo JSON incluye `ruteoProveedorActivo`, `proveedorRuteoNombre`, `sucursalMetaKey` y `sucursalMetaSeparador` con sus valores reales

#### Scenario: GET de tenant sin ruteo configurado

- GIVEN un tenant con `RuteoProveedorActivo=false` (default)
- WHEN `GET /api/tenants/{slug}` responde
- THEN el cuerpo JSON incluye los 4 campos con sus valores por defecto (`false` y strings vacíos), no los omite

---

### Requirement: Formulario /tenants expone y valida campos de ruteo

El formulario Blazor de tenants DEBE mostrar los 4 campos de ruteo. La validación DEBE ser condicional: `ProveedorRuteoNombre` y `SucursalMetaKey` son requeridos solo cuando `RuteoProveedorActivo` está activado. Al guardar con datos inválidos, el formulario DEBE mostrar el error y NO persistir los cambios. La validación DEBE delegarse en `TenantAppService` (no duplicar reglas en el componente).

#### Scenario: Guardar con ruteo activo y campos completos desde UI

- GIVEN el formulario `/tenants` con `RuteoProveedorActivo` activado, `ProveedorRuteoNombre` y `SucursalMetaKey` completados
- WHEN el usuario envía el formulario
- THEN la operación es exitosa y los datos se persisten via `TenantAppService`

#### Scenario: Guardar con ruteo activo y campo obligatorio vacío desde UI

- GIVEN el formulario `/tenants` con `RuteoProveedorActivo` activado y `SucursalMetaKey` vacío
- WHEN el usuario envía el formulario
- THEN el formulario muestra el error devuelto por `TenantAppService` (`IsSuccess=false`)
- AND los datos previos del tenant no cambian

#### Scenario: SucursalMetaSeparador siempre opcional en UI

- GIVEN el formulario `/tenants` con ruteo activo y `SucursalMetaSeparador` vacío
- WHEN el usuario envía el formulario con `ProveedorRuteoNombre` y `SucursalMetaKey` completos
- THEN la operación es exitosa; `SucursalMetaSeparador` se guarda como string vacío sin error

---

## MODIFIED Requirements

### Requirement: Editar Tenant — Propiedades editables y UpdatedAt

El sistema DEBE permitir editar `Name`, `TargetUrl`, `SignatureScheme`, los 4 campos de ruteo (`RuteoProveedorActivo`, `ProveedorRuteoNombre`, `SucursalMetaKey`, `SucursalMetaSeparador`) y otras propiedades que no sean el slug. DEBE actualizar `UpdatedAt` con la fecha/hora UTC de la operación. DEBE rechazar ediciones con `TargetUrl` vacía o inválida.
(Previamente: solo se mencionaban `Name`, `TargetUrl`, `SignatureScheme` como propiedades editables; los 4 campos de ruteo no estaban expuestos.)

#### Scenario: Edición exitosa

- GIVEN un tenant existente
- WHEN se invoca `UpdateAsync` con `Name` y `TargetUrl` válidos
- THEN la operación es exitosa, las propiedades se actualizan y `UpdatedAt` refleja el momento UTC de la operación

#### Scenario: TargetUrl inválida en edición

- GIVEN un tenant existente
- WHEN se invoca `UpdateAsync` con `TargetUrl` vacía o con formato inválido
- THEN retorna `Result` con `IsSuccess = false` y error indicando URL inválida

#### Scenario: Tenant no encontrado en edición

- GIVEN un identificador de tenant que no existe
- WHEN se invoca `UpdateAsync`
- THEN retorna `Result` con `IsSuccess = false` y error de tenant no encontrado

#### Scenario: Edición de campos de ruteo

- GIVEN un tenant existente con `RuteoProveedorActivo=false`
- WHEN se invoca `UpdateAsync` con `RuteoProveedorActivo=true`, `ProveedorRuteoNombre="woocommerce-multisucursal"` y `SucursalMetaKey="_woosea_item_id"`
- THEN los 3 campos se actualizan, `UpdatedAt` se renueva y la operación retorna `IsSuccess=true`

---

### Requirement: Preservación del contrato HTTP

Los endpoints existentes de la API Minimal (`TenantApiEndpoints`) DEBEN preservar exactamente sus status codes: `200 OK`, `201 Created`, `400 Bad Request`, `404 Not Found` y `409 Conflict`. El nuevo campo `400` por validación de ruteo usa el mismo mecanismo que los `400` ya existentes. Delegar en `TenantAppService` NO DEBE cambiar ningún status code ni cuerpo de respuesta observable para requests que no incluyan los nuevos campos.
(Previamente: no contemplaba el caso `400` por campos de ruteo inválidos.)

#### Scenario: No regresión en tests de integración HTTP

- GIVEN los ~19 tests de integración HTTP existentes sobre `TenantApiEndpoints` con `WebApplicationFactory`
- WHEN se ejecuta `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`
- THEN todos los tests pasan sin modificaciones en el código de test

#### Scenario: Status 409 por slug duplicado desde API

- GIVEN un tenant con slug `"mi-tenant"` ya registrado
- WHEN la API recibe `POST /api/tenants` con el mismo slug
- THEN responde `409 Conflict`

#### Scenario: Status 400 por slug inválido desde API

- GIVEN una solicitud `POST /api/tenants` con slug que no cumple `SlugRegex`
- WHEN la API procesa la solicitud delegando en `TenantAppService`
- THEN responde `400 Bad Request`

#### Scenario: Status 400 por ruteo inválido desde API

- GIVEN una solicitud `POST /api/tenants` o `PUT /api/tenants/{slug}` con `RuteoProveedorActivo=true` y campos obligatorios ausentes o `ProveedorRuteoNombre` inválido
- WHEN la API procesa la solicitud
- THEN responde `400 Bad Request` con mensaje descriptivo del error de validación
