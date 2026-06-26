# woocommerce-multisucursal-routing — Especificación

**Versión**: 1.0
**Estado**: Borrador — pendiente de design y aplicación
**Propietario**: Dotar Gateway
**Audiencia**: Arquitectura, implementación, verificación

---

## Propósito

Ruteo dinámico de pedidos WooCommerce a la máquina física de cada sucursal, mediante un nuevo `WooCommerceMultiSucursalProvider`. El tenant se resuelve por slug de URL (1 WordPress = 1 tenant); la sucursal se extrae de una key configurable del `meta_data` del pedido. Reutiliza la infraestructura de ruteo dinámico existente (`CajaRegistrada`, `WebhookDispatcherWorker`, firma `X-Caja-Signature`) sin modificarla.

---

## Requisitos

### Requirement: Validación de firma entrante WooCommerce

El sistema DEBE validar la firma del webhook entrante usando el header `X-WC-Webhook-Signature`. La firma DEBE calcularse como HMAC-SHA256 del payload RAW codificado en base64, usando el `WebhookSecret` del tenant. La validación DEBE ser timing-safe. Un webhook cuya firma no supere la validación DEBE ser rechazado con `401 Unauthorized`; el webhook NO se encola y el rechazo se registra en `SystemLogs` con categoría `Ingest`. Un webhook sin el header `X-WC-Webhook-Signature` DEBE ser tratado como firma inválida (mismo rechazo).

#### Scenario: Firma válida — webhook aceptado y encolado

- GIVEN un tenant con slug `"tienda-norte"` y `WebhookSecret = "s3cr3t"`
- AND un payload RAW cuya firma HMAC-SHA256 en base64 coincide con `X-WC-Webhook-Signature`
- WHEN llega `POST /ingest/tienda-norte` con ese header y payload
- THEN el sistema acepta el webhook, lo encola con `ProveedorNombre = "woocommerce-multisucursal"` y retorna `202 Accepted`

#### Scenario: Firma inválida — webhook rechazado

- GIVEN un tenant con slug `"tienda-norte"` y `WebhookSecret = "s3cr3t"`
- WHEN llega `POST /ingest/tienda-norte` con `X-WC-Webhook-Signature` que NO corresponde al payload
- THEN el sistema retorna `401 Unauthorized`, el webhook NO se encola y se registra en `SystemLogs` con categoría `Ingest`

#### Scenario: Header ausente — webhook rechazado

- GIVEN un tenant con slug `"tienda-norte"`
- WHEN llega `POST /ingest/tienda-norte` sin el header `X-WC-Webhook-Signature`
- THEN el sistema retorna `401 Unauthorized`, el webhook NO se encola y se registra en `SystemLogs` con categoría `Ingest`

---

### Requirement: Resolución de tenant por slug de URL

El sistema DEBE resolver el tenant buscando por el slug de la URL (`POST /ingest/{slug}`). NO DEBE usar `ProveedorWebhookConfig.CuentaExternaId` para WooCommerce (1 WordPress = 1 tenant, sin multi-cuenta). Si el slug no existe o el tenant no tiene `ProveedorNombre = "woocommerce-multisucursal"` configurado, el sistema DEBE retornar `404 Not Found` y registrar en `SystemLogs` con categoría `Ingest`.

#### Scenario: Slug existente con proveedor configurado — tenant resuelto

- GIVEN un tenant con slug `"tienda-norte"` y proveedor `"woocommerce-multisucursal"` configurado
- WHEN llega un webhook a `POST /ingest/tienda-norte`
- THEN el sistema resuelve el tenant y continúa con la validación de firma

#### Scenario: Slug inexistente — rechazo 404

- GIVEN que no existe ningún tenant con slug `"tienda-fantasma"`
- WHEN llega un webhook a `POST /ingest/tienda-fantasma`
- THEN el sistema retorna `404 Not Found` y registra en `SystemLogs` con categoría `Ingest`

---

### Requirement: Extracción de sucursal desde meta_data configurable

El sistema DEBE leer la sucursal del pedido desde el campo `meta_data` del payload WooCommerce. La key del `meta_data` a leer DEBE ser configurable por tenant (no hardcodeada). Si el tenant tiene un separador configurado, el sistema DEBE tomar la parte izquierda del primer separador encontrado en el value; si no hay separador configurado, DEBE usar el value completo. El resultado DEBE ser la routing key (identificador de sucursal). El valor obtenido no puede ser nulo ni vacío.

#### Scenario: Extracción exitosa sin separador

- GIVEN un tenant con `MetaDataKeySucursal = "sucursal_codigo"` y sin separador configurado
- AND un payload con `meta_data: [{"key": "sucursal_codigo", "value": "SUC-NORTE"}]`
- WHEN el provider extrae la routing key
- THEN el resultado es `"SUC-NORTE"`

#### Scenario: Extracción exitosa con separador (estilo MultiLocal)

- GIVEN un tenant con `MetaDataKeySucursal = "sucursal_codigo"` y separador `"__"`
- AND un payload con `meta_data: [{"key": "sucursal_codigo", "value": "SUC-NORTE__20260625"}]`
- WHEN el provider extrae la routing key
- THEN el resultado es `"SUC-NORTE"` (parte izquierda del primer `"__"`)

#### Scenario: Cambio de key — soporte a otro plugin sin modificar código

- GIVEN un tenant con `MetaDataKeySucursal = "branch_id"` (plugin diferente)
- AND un payload con `meta_data: [{"key": "branch_id", "value": "SUCURSAL-02"}]`
- WHEN el provider extrae la routing key
- THEN el resultado es `"SUCURSAL-02"`, sin ningún cambio en el código del provider

---

### Requirement: Pedido no ruteable — dead-letter con log de severidad alta

Cuando el provider no puede determinar la routing key, o cuando la sucursal extraída no tiene una `CajaRegistrada` vigente, el sistema DEBE enviar el webhook a dead-letter y DEBE registrar en `SystemLogs` con categoría `Forward` y severidad alta, visible en `/logs`. El worker DEBE continuar procesando mensajes posteriores. Los casos no ruteables son:

| Caso | Condición |
|------|-----------|
| `meta_data` ausente | El payload no contiene el array `meta_data` |
| Key configurada no encontrada | La key `MetaDataKeySucursal` no aparece en `meta_data` |
| Value vacío | La key existe pero su value es nulo, vacío o solo espacios |
| Sucursal no registrada | La routing key no tiene `CajaRegistrada` con ese `Identificador` en el padrón del tenant |
| Registro vencido (TTL) | La `CajaRegistrada` existe pero su TTL expiró |

#### Scenario: meta_data ausente en payload

- GIVEN un payload WooCommerce sin el array `meta_data`
- WHEN el provider intenta extraer la routing key
- THEN el webhook va a dead-letter y se registra en `SystemLogs` con categoría `Forward` y severidad alta

#### Scenario: Key configurada no encontrada en meta_data

- GIVEN un tenant con `MetaDataKeySucursal = "sucursal_codigo"`
- AND un payload cuyo `meta_data` no contiene ningún objeto con `key = "sucursal_codigo"`
- WHEN el provider intenta extraer la routing key
- THEN el webhook va a dead-letter y se registra en `SystemLogs` con categoría `Forward` y severidad alta

#### Scenario: Value vacío en la key configurada

- GIVEN un tenant con `MetaDataKeySucursal = "sucursal_codigo"`
- AND un payload con `meta_data: [{"key": "sucursal_codigo", "value": ""}]`
- WHEN el provider intenta extraer la routing key
- THEN el webhook va a dead-letter y se registra en `SystemLogs` con categoría `Forward` y severidad alta

#### Scenario: Sucursal extraída no registrada en el padrón

- GIVEN que `"SUC-DESCONOCIDA"` no existe en `CajaRegistrada` para el tenant
- AND un payload con routing key resultante `"SUC-DESCONOCIDA"`
- WHEN el worker busca la caja en el padrón
- THEN el webhook va a dead-letter y se registra en `SystemLogs` con categoría `Forward` y severidad alta

#### Scenario: Registro de sucursal con TTL vencido

- GIVEN que `"SUC-NORTE"` existe en `CajaRegistrada` pero su TTL expiró
- AND un pedido con routing key `"SUC-NORTE"`
- WHEN el worker busca la caja en el padrón
- THEN el webhook va a dead-letter y se registra en `SystemLogs` con categoría `Forward` y severidad alta

---

### Requirement: Ruteo de eventos de pedido al destino de la sucursal

Para los eventos `order.created`, `order.updated` y `order.deleted`, el sistema DEBE extraer la routing key y reenviar el payload RAW del pedido a la `CallbackUrl` de la `CajaRegistrada` correspondiente, firmado con `X-Caja-Signature` (HMAC-SHA256 del payload RAW con el `WebhookSecret` del tenant, en hex lowercase). El sistema NO DEBE llamar a ninguna API de WooCommerce durante este flujo.

#### Scenario: order.created ruteado exitosamente

- GIVEN un pedido `order.created` con routing key `"SUC-NORTE"` y una `CajaRegistrada` con `Identificador = "SUC-NORTE"` y `CallbackUrl = "https://caja-norte.local/webhook"` vigente
- WHEN el worker procesa el webhook
- THEN el sistema envía `POST https://caja-norte.local/webhook` con el payload RAW y header `X-Caja-Signature` correcto; retorna `202 Accepted` al ingest original

#### Scenario: order.updated ruteado exitosamente

- GIVEN un pedido `order.updated` con routing key `"SUC-NORTE"` y caja vigente
- WHEN el worker procesa el webhook
- THEN el sistema envía el payload RAW a la `CallbackUrl` de `"SUC-NORTE"` con `X-Caja-Signature` correcto

#### Scenario: order.deleted ruteado exitosamente

- GIVEN un pedido `order.deleted` con routing key `"SUC-NORTE"` y caja vigente
- WHEN el worker procesa el webhook
- THEN el sistema envía el payload RAW a la `CallbackUrl` de `"SUC-NORTE"` con `X-Caja-Signature` correcto

---

### Requirement: Comportamiento ante eventos que no son de pedido

El sistema DEBE ignorar eventos WooCommerce que no sean `order.created`, `order.updated` ni `order.deleted`. Los eventos ignorados DEBEN registrarse en `SystemLogs` con categoría `Ingest` para trazabilidad y NO DEBEN encolarse ni enviarse a dead-letter.

#### Scenario: Evento no-pedido ignorado y registrado

- GIVEN un webhook WooCommerce con topic `"product.updated"`
- WHEN llega al endpoint de ingest con firma válida
- THEN el sistema responde `200 OK`, no encola el webhook y registra en `SystemLogs` con categoría `Ingest` que el evento fue ignorado por ser fuera de alcance

---

### Requirement: No regresión de flujos existentes

El nuevo provider DEBE ser aditivo. El flujo 1-a-1 (`POST /ingest/{slug}` sin proveedor configurado), el ruteo MercadoPago y los tenants productivos actuales DEBEN quedar intactos. El código del `WebhookDispatcherWorker` y la interfaz `IWebhookProvider` NO DEBEN modificarse para acomodar la lógica WooCommerce.

#### Scenario: Flujo 1-a-1 sin cambios para tenant sin proveedor

- GIVEN un tenant SIN `ProveedorNombre = "woocommerce-multisucursal"` configurado (ej. tenant WooCommerce clásico con `TargetUrl` fija)
- WHEN llega un webhook a `POST /ingest/{slug}` para ese tenant
- THEN el flujo 1-a-1 existente se ejecuta sin cambios y el nuevo provider no interviene

#### Scenario: Ruteo MercadoPago intacto

- GIVEN un `QueuedWebhook` con `ProveedorNombre = "mercadopago"`
- WHEN el worker lo procesa
- THEN el worker resuelve `MercadoPagoProvider` y ejecuta el flujo MP sin cambios; `WooCommerceMultiSucursalProvider` no interviene

---

## Invariantes del contrato

| Elemento | Valor |
|----------|-------|
| Header de firma entrante | `X-WC-Webhook-Signature` |
| Algoritmo firma entrante | HMAC-SHA256 del payload RAW, codificado en base64 |
| Resolución de tenant | Por slug de URL (`/ingest/{slug}`); sin `CuentaExternaId` |
| Key de `meta_data` | Configurable por tenant; no hardcodeada |
| Separador en value | Configurable por tenant; opcional; toma parte izquierda si presente |
| Header de firma de salida | `X-Caja-Signature` |
| Algoritmo firma de salida | HMAC-SHA256 del payload RAW en hex lowercase |
| Body reenviado | RAW del pedido WooCommerce (sin enriquecimiento) |
| Keyed DI del provider | `"woocommerce-multisucursal"` |
| Infra reutilizada sin cambios | `CajaRegistrada`, `WebhookDispatcherWorker`, `CajaRegistradaCacheService`, `IWebhookProvider` |

---

## Non-goals (fuera de alcance — explícitos)

- Enriquecimiento contra la API de WooCommerce cuando falta el `meta_data` → mejora futura.
- Fan-out a múltiples sucursales por pedido → v1 es 1 pedido → 1 sucursal.
- Alerta push (email/Slack/webhook) para pedidos no ruteables → v1 usa SystemLog de severidad alta.
- Resolución multi-cuenta (`CuentaExterna`) → no aplica; 1 WordPress = 1 tenant.
- Tooling/UI de administración de la key del `meta_data` → fuera del primer slice.
- Acoplamiento a un plugin concreto (MultiLocal u otro) → la key es configurable.
