# webhook-provider-routing — Especificación

## Propósito

Abstracción genérica `IWebhookProvider` que desacopla la lógica de enriquecimiento y extracción de routing key del núcleo del worker. El worker orquesta el flujo; cada impl de proveedor encapsula la semántica específica (MP, Stripe, etc.). Esta especificación describe el contrato observable del flujo completo, no la implementación interna.

---

## Requirements

### Requirement: Endpoint dedicado por proveedor — resolución de tenant por CuentaExternaId

El sistema DEBE exponer `POST /webhook/{proveedor}` (ej. `/webhook/mercadopago`) como punto de entrada ÚNICO para webhooks de ese proveedor. El slug de tenant NO forma parte de esta ruta. El sistema DEBE resolver el tenant buscando en `ProveedorWebhookConfig` la entrada cuyo `(ProveedorNombre, CuentaExternaId)` coincida con la cuenta externa extraída del payload entrante. Si ninguna configuración coincide, el sistema DEBE retornar `404 Not Found` y registrar el evento en `SystemLogs` sin encolar el webhook.

#### Scenario: Resolución exitosa de tenant por CuentaExternaId

- GIVEN una `ProveedorWebhookConfig` para `(ProveedorNombre: "mercadopago", CuentaExternaId: "123456789")`
- WHEN llega un webhook a `POST /webhook/mercadopago` con `user_id = "123456789"` en el payload
- THEN el sistema resuelve el tenant correspondiente y continúa con la validación de firma

#### Scenario: CuentaExternaId desconocida produce rechazo

- GIVEN que ninguna `ProveedorWebhookConfig` tiene `CuentaExternaId = "999999999"` para `"mercadopago"`
- WHEN llega un webhook a `POST /webhook/mercadopago` con `user_id = "999999999"`
- THEN el endpoint retorna `404 Not Found`, el webhook NO se encola y se registra en `SystemLogs` con categoría `Ingest`

#### Scenario: No regresión — tenants sin proveedor siguen usando POST /ingest/{slug}

- GIVEN un tenant SIN `ProveedorWebhookConfig` configurada (flujo WooCommerce clásico)
- WHEN llega un webhook a `POST /ingest/{slug}` para ese tenant
- THEN el flujo 1-a-1 existente se ejecuta sin cambios; el endpoint `/webhook/{proveedor}` no interviene

---

### Requirement: Validación de firma del webhook entrante por proveedor

El sistema DEBE validar la firma del webhook entrante usando el secret del proveedor almacenado en `ProveedorWebhookConfig.SecretProveedor` de la configuración resuelta. Cada proveedor define su propio esquema de firma (ej. MP usa `x-signature` con timestamp + secret). La validación DEBE ocurrir ANTES de encolar el webhook. Un webhook cuya firma no supere la validación DEBE ser rechazado con `401 Unauthorized`, no se encola y se registra en `SystemLogs` con categoría `Ingest`.

#### Scenario: Webhook MP con firma válida es encolado

- GIVEN una `ProveedorWebhookConfig` para `"mercadopago"` con `CuentaExternaId` que coincide con el `user_id` del payload
- WHEN llega un webhook MP con header `x-signature` válido para el `SecretProveedor` configurado
- THEN el webhook es encolado con `ProveedorNombre = "mercadopago"` y el ingest retorna `202 Accepted`

#### Scenario: Webhook MP con firma inválida es rechazado

- GIVEN una `ProveedorWebhookConfig` resuelta para el tenant
- WHEN llega un webhook MP con `x-signature` que no corresponde al `SecretProveedor` de la config
- THEN el ingest retorna `401 Unauthorized`, el webhook NO se encola y se registra un log con categoría `Ingest`

---

### Requirement: Enriquecimiento por proveedor

El sistema DEBE llamar a la API del proveedor externo (ej. `GET /v1/payments/{id}` en MP) usando las credenciales de `ProveedorWebhookConfig` del tenant para obtener los datos completos del pago. El enriquecimiento ocurre en el worker, NO en el ingest. El payload enriquecido DEBE usarse únicamente para extraer la routing key; NO DEBE ser reenviado a la caja.

#### Scenario: Enriquecimiento exitoso extrae datos del pago

- GIVEN un `QueuedWebhook` con `ProveedorNombre = "mercadopago"` y payload RAW `{"topic":"payment","id":"12345"}`
- WHEN el worker llama al enriquecimiento con las credenciales del tenant
- THEN se retorna el objeto completo del pago desde la API de MP, incluyendo `external_reference`

#### Scenario: Error de enriquecimiento produce dead-letter

- GIVEN un `QueuedWebhook` con `ProveedorNombre = "mercadopago"`
- WHEN la API de MP retorna error (timeout, 4xx, 5xx) durante el enriquecimiento
- THEN el webhook va a dead-letter, se registra el error en `SystemLogs` con categoría `Forward` y NO se intenta reenviar a ninguna caja

---

### Requirement: Extracción de routing key mediante separador `::`

El sistema DEBE extraer la routing key tomando la porción del campo `external_reference` anterior al primer `::`. El `identificadorCaja` resultante es una string OPACA (puede contener guiones u otros caracteres); el gateway lo compara EXACTO contra el padrón, sin sub-parsear su contenido. El formato esperado de `external_reference` es `{identificadorCaja}::{comprobante}`, donde `identificadorCaja` NO puede contener `::`. Si `external_reference` está ausente, no contiene `::`, o la porción anterior al primer `::` es vacía, el webhook DEBE ir a dead-letter.

#### Scenario: Extracción exitosa de routing key con separador `::`

- GIVEN un pago enriquecido con `external_reference = "SUC1-C01::00001234"`
- WHEN el proveedor extrae la routing key
- THEN el resultado es `"SUC1-C01"` (porción anterior al primer `::`)

#### Scenario: Identificador opaco con guiones internos se matchea correctamente

- GIVEN un pago enriquecido con `external_reference = "CAJA-ESPECIAL-01::00005678"` y una `CajaRegistrada` con `Identificador = "CAJA-ESPECIAL-01"`
- WHEN el proveedor extrae la routing key y el worker busca en el padrón
- THEN el resultado de la extracción es `"CAJA-ESPECIAL-01"` y el lookup encuentra la caja (comparación exacta, sin sub-parseo)

#### Scenario: external_reference sin `::` produce dead-letter

- GIVEN un pago enriquecido con `external_reference = "SUC1-C01-00001234"` (sin separador `::`)
- WHEN el proveedor intenta extraer la routing key
- THEN el webhook va a dead-letter con log categoría `Forward` y NO se reenvía

#### Scenario: external_reference ausente produce dead-letter

- GIVEN un pago enriquecido sin campo `external_reference` (o con valor nulo)
- WHEN el proveedor intenta extraer la routing key
- THEN el webhook va a dead-letter con log categoría `Forward` y NO se reenvía

#### Scenario: Porción anterior al `::` vacía produce dead-letter

- GIVEN un pago enriquecido con `external_reference = "::comprobante"` (identificadorCaja vacío)
- WHEN el proveedor intenta extraer la routing key
- THEN el webhook va a dead-letter con log categoría `Forward` y NO se reenvía

---

### Requirement: Ruteo al destino correcto — reenvío RAW firmado HMAC

El sistema DEBE buscar la caja en el padrón del tenant por `(TenantId, routing key)` (comparación exacta) y reenviar el payload RAW original del proveedor (topic + id) a la `callbackUrl` de esa caja, firmado HMAC con el `WebhookSecret` del tenant. El payload enriquecido NO DEBE ser reenviado.

#### Scenario: Reenvío exitoso a caja encontrada

- GIVEN que `"SUC1-C01"` existe en el padrón del tenant con `CallbackUrl = "https://caja1.dotarsoluciones.com/callback"`
- WHEN el worker completa el flujo para un pago con `external_reference = "SUC1-C01::00001234"`
- THEN se envía un POST a `"https://caja1.dotarsoluciones.com/callback"` con el payload RAW de MP y header `X-Caja-Signature` firmado con el `WebhookSecret` del tenant

#### Scenario: Caja no encontrada produce dead-letter sin fallback

- GIVEN que `"SUC1-C99"` NO existe en el padrón del tenant
- WHEN el worker completa el enriquecimiento y extrae routing key `"SUC1-C99"`
- THEN el webhook va a dead-letter, se registra en `SystemLogs` con categoría `Forward` y NO se reenvía a `Tenant.TargetUrl` ni a ningún otro destino

---

### Requirement: Semántica de proveedor encapsulada — núcleo genérico

El sistema NO DEBE contener lógica específica de MercadoPago (extracción de `external_reference`, formato de `x-signature`, URL de API de MP, extracción de `CuentaExternaId` del payload) fuera de la impl `MercadoPagoProvider`. El núcleo del worker DEBE operar únicamente sobre la interfaz `IWebhookProvider`. Agregar un nuevo proveedor DEBE requerir solo una nueva impl, sin modificar el worker ni el núcleo.

#### Scenario: Nuevo proveedor sin cambio en el worker

- GIVEN que se registra una nueva impl `IWebhookProvider` para proveedor `"stripe"` via keyed DI
- WHEN el worker procesa un `QueuedWebhook` con `ProveedorNombre = "stripe"`
- THEN el worker resuelve la impl correcta y ejecuta el flujo sin ninguna ramificación condicional basada en el nombre del proveedor en el código del worker

---

### Requirement: Dead-letter no bloquea el procesamiento del worker

Cuando un webhook va a dead-letter (por cualquier causa: CuentaExternaId desconocida, firma inválida, error de enriquecimiento, routing key inválida, caja no encontrada), el worker DEBE continuar procesando los siguientes mensajes de la cola. El dead-letter DEBE registrarse en `SystemLogs` y DEBE actualizar el `DeliveryStatus` del `QueuedWebhook` al estado correspondiente.

#### Scenario: Worker continúa tras dead-letter

- GIVEN que el webhook `A` va a dead-letter por caja no encontrada
- WHEN el worker termina de procesar `A`
- THEN el worker consume el siguiente mensaje de la cola sin detenerse y el `DeliveryStatus` de `A` refleja el estado de dead-letter
