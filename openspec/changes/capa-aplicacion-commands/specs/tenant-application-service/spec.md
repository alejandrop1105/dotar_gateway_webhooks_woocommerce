# tenant-application-service — Especificación

## Propósito

Capa de aplicación que centraliza las reglas de negocio de tenants. `TenantAppService` es la única fuente de verdad para crear, editar, borrar y activar/desactivar tenants. La API Minimal y el dashboard Blazor delegan en este service; ninguno accede directamente a `GatewayDbContext` para estas operaciones.

---

## Requirements

### Requirement: Tipo Result

El sistema DEBE exponer un tipo `Result<T>` propio (sealed record) con propiedades `IsSuccess`, `Error` y `Value`. Ninguna operación del service DEBE lanzar excepciones para casos de negocio; DEBE devolver un `Result<T>` con `IsSuccess = false` y un mensaje de error descriptivo.

#### Scenario: Operación exitosa

- GIVEN un servicio que completa una operación correctamente
- WHEN el método async retorna
- THEN `Result.IsSuccess` es `true`, `Value` contiene el dato resultante y `Error` es nulo o vacío

#### Scenario: Error de negocio

- GIVEN una operación que falla por regla de negocio (slug inválido, duplicado, no encontrado, URL inválida)
- WHEN el método async retorna
- THEN `Result.IsSuccess` es `false`, `Error` contiene un mensaje descriptivo y no se lanza ninguna excepción

---

### Requirement: Crear Tenant — Validación de campos requeridos

El sistema DEBE rechazar la creación si `Name`, `Slug` o `TargetUrl` están vacíos o nulos.

#### Scenario: Campos requeridos ausentes

- GIVEN una solicitud de creación con `Name`, `Slug` o `TargetUrl` vacío
- WHEN se invoca `CreateAsync`
- THEN retorna `Result` con `IsSuccess = false` y error que indica el campo faltante

---

### Requirement: Crear Tenant — Normalización y validación de slug

El sistema DEBE normalizar el slug aplicando `ToLowerInvariant().Trim()` antes de cualquier validación. El slug normalizado DEBE cumplir el patrón `^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$`. Esta validación DEBE aplicarse tanto cuando la solicitud proviene de la API como cuando proviene del dashboard.

#### Scenario: Slug normalizado válido

- GIVEN un slug con letras mayúsculas o espacios extremos, p.ej. `" Mi-Tenant "`
- WHEN se invoca `CreateAsync`
- THEN el slug almacenado es `"mi-tenant"` (normalizado) y la operación es exitosa

#### Scenario: Slug con formato inválido tras normalización

- GIVEN un slug que tras normalización no cumple `SlugRegex`, p.ej. `"with space"` o `"UPPER"`
- WHEN se invoca `CreateAsync`
- THEN retorna `Result` con `IsSuccess = false` y error indicando formato de slug inválido

#### Scenario: Slug inválido desde el dashboard

- GIVEN una solicitud de creación iniciada desde el componente Blazor con slug mal formado
- WHEN se delega en `TenantAppService.CreateAsync`
- THEN retorna `Result` con `IsSuccess = false` (mismo comportamiento que desde la API)

---

### Requirement: Crear Tenant — Unicidad de slug

El sistema DEBE rechazar la creación si ya existe un tenant con el mismo slug normalizado.

#### Scenario: Slug duplicado

- GIVEN un tenant con slug `"mi-tenant"` ya registrado
- WHEN se invoca `CreateAsync` con slug `"mi-tenant"`
- THEN retorna `Result` con `IsSuccess = false` y error de slug duplicado

---

### Requirement: Crear Tenant — Generación de WebhookSecret

El sistema DEBE generar un `WebhookSecret` en base64 de 32 bytes aleatorios cuando el `SignatureScheme` no es `None`. Cuando el esquema es `None`, el secret DEBE ser una cadena vacía.

#### Scenario: Secret generado para esquema HMAC

- GIVEN una solicitud de creación con `SignatureScheme` distinto de `None`
- WHEN se invoca `CreateAsync` y la operación es exitosa
- THEN el tenant persiste con `WebhookSecret` en formato base64 de longitud correspondiente a 32 bytes

#### Scenario: Secret vacío para esquema None

- GIVEN una solicitud de creación con `SignatureScheme = None`
- WHEN se invoca `CreateAsync` y la operación es exitosa
- THEN el tenant persiste con `WebhookSecret` igual a cadena vacía

---

### Requirement: Crear Tenant — Invalidación de caché

El sistema DEBE invalidar la entrada de caché del tenant después de una creación exitosa.

#### Scenario: Caché invalidada tras creación

- GIVEN un `TenantCacheService` activo
- WHEN `CreateAsync` completa con éxito
- THEN la caché del slug creado es invalidada

---

### Requirement: Editar Tenant — Slug inmutable

El slug DEBE ser inmutable tras la creación. La operación de edición NO DEBE exponer el slug como campo editable: el contrato de entrada de actualización (`UpdateTenantInput`) no incluye slug, por lo que el cambio de slug es **imposible por construcción**, no algo que se valide o se ignore en runtime. El slug identifica el recurso a editar (se pasa como parámetro de identificación, no como dato modificable) y permanece igual al de la creación.

#### Scenario: El contrato de edición no admite cambio de slug

- GIVEN un tenant con slug `"mi-tenant"`
- WHEN se invoca `UpdateAsync("mi-tenant", input)` siendo `input` un `UpdateTenantInput` que no contiene campo slug
- THEN la operación edita solo las propiedades mutables y el slug del tenant permanece `"mi-tenant"` por construcción del contrato

---

### Requirement: Editar Tenant — Propiedades editables y UpdatedAt

El sistema DEBE permitir editar `Name`, `TargetUrl`, `SignatureScheme` y otras propiedades que no sean el slug. DEBE actualizar `UpdatedAt` con la fecha/hora UTC de la operación. DEBE rechazar ediciones con `TargetUrl` vacía o inválida.

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

---

### Requirement: Editar Tenant — Invalidación de caché

El sistema DEBE invalidar la entrada de caché del tenant después de una edición exitosa.

#### Scenario: Caché invalidada tras edición

- GIVEN un tenant en caché
- WHEN `UpdateAsync` completa con éxito
- THEN la caché del slug correspondiente es invalidada

---

### Requirement: Borrar Tenant

El sistema DEBE eliminar el tenant de la base de datos. DEBE retornar error si el tenant no existe. DEBE invalidar la caché tras la eliminación exitosa.

#### Scenario: Borrado exitoso

- GIVEN un tenant existente con slug `"mi-tenant"`
- WHEN se invoca `DeleteAsync`
- THEN el tenant es eliminado, la operación retorna `IsSuccess = true` y la caché es invalidada

#### Scenario: Tenant no encontrado en borrado

- GIVEN un identificador de tenant que no existe
- WHEN se invoca `DeleteAsync`
- THEN retorna `Result` con `IsSuccess = false` y error de tenant no encontrado

---

### Requirement: Toggle activo

El sistema DEBE invertir el valor de `IsActive` del tenant. DEBE actualizar `UpdatedAt`. DEBE invalidar la caché. DEBE retornar error si el tenant no existe.

#### Scenario: Toggle de activo a inactivo

- GIVEN un tenant con `IsActive = true`
- WHEN se invoca `ToggleActiveAsync`
- THEN `IsActive` pasa a `false`, `UpdatedAt` se actualiza y la caché es invalidada

#### Scenario: Toggle de inactivo a activo

- GIVEN un tenant con `IsActive = false`
- WHEN se invoca `ToggleActiveAsync`
- THEN `IsActive` pasa a `true`, `UpdatedAt` se actualiza y la caché es invalidada

#### Scenario: Tenant no encontrado en toggle

- GIVEN un identificador de tenant que no existe
- WHEN se invoca `ToggleActiveAsync`
- THEN retorna `Result` con `IsSuccess = false` y error de tenant no encontrado

---

### Requirement: Preservación del contrato HTTP

Los endpoints existentes de la API Minimal (`TenantApiEndpoints`) DEBEN preservar exactamente sus status codes: `200 OK`, `201 Created`, `400 Bad Request`, `404 Not Found` y `409 Conflict`. Delegar en `TenantAppService` NO DEBE cambiar ningún status code ni cuerpo de respuesta observable.

#### Scenario: No regresión en tests de integración HTTP

- GIVEN los ~19 tests de integración HTTP existentes sobre `TenantApiEndpoints` con `WebApplicationFactory`
- WHEN se ejecuta `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`
- THEN todos los tests pasan sin modificaciones en el código de test

#### Scenario: Status 409 por slug duplicado desde API

- GIVEN un tenant con slug `"mi-tenant"` ya registrado
- WHEN la API recibe `POST /api/tenants` con el mismo slug
- THEN responde `409 Conflict` (mismo comportamiento que antes de la refactorización)

#### Scenario: Status 400 por slug inválido desde API

- GIVEN una solicitud `POST /api/tenants` con slug que no cumple `SlugRegex`
- WHEN la API procesa la solicitud delegando en `TenantAppService`
- THEN responde `400 Bad Request` (mismo comportamiento que antes de la refactorización)
