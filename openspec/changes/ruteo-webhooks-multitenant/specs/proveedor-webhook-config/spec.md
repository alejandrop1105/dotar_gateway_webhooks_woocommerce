# proveedor-webhook-config — Especificación

## Propósito

Almacena y gestiona las credenciales, el secret del proveedor y la cuenta externa asociada para que el gateway pueda (1) resolver el tenant a partir del `user_id`/cuenta del payload entrante y (2) llamar a la API del proveedor en nombre del tenant. Las credenciales se almacenan cifradas; el lookup por `(ProveedorNombre, CuentaExternaId)` y por `(TenantId, ProveedorNombre)` sirven al hot path del endpoint de ingesta y del worker respectivamente.

---

## Requirements

### Requirement: Alta y actualización de configuración de proveedor

El sistema DEBE permitir crear o actualizar la configuración de un proveedor para un tenant dado a través de la API de administración. La operación DEBE ser idempotente: si ya existe una entrada para `(TenantId, ProveedorNombre)`, la actualización DEBE sobrescribir las credenciales, la `BaseUrl`, el `SecretProveedor` y la `CuentaExternaId`, y actualizar `ActualizadaEn`. Si no existe, DEBE crearla. Los campos `SecretProveedor` (para validar firma entrante) y `CuentaExternaId` (para resolver el tenant desde el payload) son OBLIGATORIOS.

#### Scenario: Alta de configuración nueva

- GIVEN un tenant existente sin configuración para `ProveedorNombre = "mercadopago"`
- WHEN se invoca la API admin con credenciales válidas, `CuentaExternaId = "123456789"` y `SecretProveedor` para ese `(TenantId, ProveedorNombre)`
- THEN se crea una entrada `ProveedorWebhookConfig` con las credenciales cifradas, `CuentaExternaId` y `HabilitadoEn` = UTC now

#### Scenario: Actualización de configuración existente

- GIVEN un tenant con configuración previa para `ProveedorNombre = "mercadopago"`
- WHEN se invoca la API admin con nuevas credenciales y nueva `CuentaExternaId` para el mismo `(TenantId, ProveedorNombre)`
- THEN las credenciales y `CuentaExternaId` son actualizadas (credenciales cifradas), `ActualizadaEn` = UTC now y no se crea un duplicado

---

### Requirement: Credenciales y secret almacenados cifrados

El sistema DEBE cifrar `CredencialesJson` y `SecretProveedor` usando `IDataProtector` antes de persistirlos. Ningún valor sensible DEBE aparecer en la base de datos en texto plano, en logs ni en respuestas de la API. El descifrado ocurre únicamente en memoria, durante el flujo de validación de firma y de enriquecimiento.

#### Scenario: Credenciales no expuestas en respuesta de API

- GIVEN una configuración de proveedor persistida con `access_token` de MP y `SecretProveedor`
- WHEN se consulta la API admin para listar o ver la configuración del proveedor
- THEN la respuesta NO contiene el `access_token`, el `SecretProveedor` ni ningún campo sensible en texto plano

#### Scenario: Credenciales no aparecen en logs de sistema

- GIVEN un enriquecimiento fallido (timeout o error HTTP de la API del proveedor)
- WHEN el sistema registra el error en `SystemLogs`
- THEN el log contiene el mensaje de error y el `TenantId`, pero NO incluye ningún valor de credencial ni el `SecretProveedor`

---

### Requirement: Lookup por (ProveedorNombre, CuentaExternaId) para resolución de tenant

El sistema DEBE exponer una operación de lookup indexada por `(ProveedorNombre, CuentaExternaId)` para que el endpoint `POST /webhook/{proveedor}` pueda resolver el tenant a partir del payload entrante. Si no existe ninguna config que coincida, DEBE retornar nulo o resultado vacío (no lanzar excepción).

#### Scenario: Lookup por CuentaExternaId resuelve tenant

- GIVEN una `ProveedorWebhookConfig` con `(ProveedorNombre: "mercadopago", CuentaExternaId: "123456789")` asociada al `TenantId: 5`
- WHEN el endpoint recibe un payload con `user_id = "123456789"` en `POST /webhook/mercadopago`
- THEN el lookup retorna la config del tenant 5, incluyendo el `SecretProveedor` descifrado para validar la firma

#### Scenario: Lookup por CuentaExternaId sin resultado retorna vacío

- GIVEN que ninguna `ProveedorWebhookConfig` tiene `CuentaExternaId = "999999999"` para `"mercadopago"`
- WHEN el endpoint hace el lookup con esa combinación
- THEN el resultado es nulo o vacío (no se lanza excepción)

---

### Requirement: Lookup por (TenantId, ProveedorNombre) para el worker

El sistema DEBE exponer una operación de lookup por `(TenantId, ProveedorNombre)` que retorna la configuración descifrada en memoria para uso del worker. Si no existe configuración para esa combinación, DEBE retornar nulo o resultado vacío (no lanzar excepción).

#### Scenario: Lookup exitoso retorna configuración descifrada

- GIVEN una `ProveedorWebhookConfig` persistida para `(TenantId: 1, ProveedorNombre: "mercadopago")`
- WHEN el worker llama al lookup con `(TenantId: 1, "mercadopago")`
- THEN el resultado contiene las credenciales descifradas y la `BaseUrl`, listas para usarse en el enriquecimiento

#### Scenario: Lookup sin configuración retorna vacío

- GIVEN que no existe `ProveedorWebhookConfig` para `(TenantId: 1, "stripe")`
- WHEN el worker llama al lookup con `(TenantId: 1, "stripe")`
- THEN el resultado es nulo o vacío (no se lanza excepción)

---

### Requirement: Restricción de acceso a la API de administración

La API de administración de configuración de proveedor DEBE requerir autenticación (API Key del gateway). Requests sin credenciales válidas DEBEN ser rechazados con `401 Unauthorized`.

#### Scenario: Rechazo sin autenticación

- GIVEN un request a la API admin de configuración de proveedor sin header de autenticación
- WHEN el endpoint procesa el request
- THEN responde `401 Unauthorized` y no modifica ningún dato
