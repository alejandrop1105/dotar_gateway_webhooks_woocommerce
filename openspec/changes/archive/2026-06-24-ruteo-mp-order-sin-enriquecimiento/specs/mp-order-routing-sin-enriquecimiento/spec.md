# mp-order-routing-sin-enriquecimiento — Especificación

## Propósito

Para notificaciones MercadoPago con `type=order` (pagos Point), el Gateway rutea directamente desde el payload RAW firmado sin llamar a la API de MP (`/v1/payments`). La routing key se extrae de `data.external_reference` del payload original. El flujo `type=payment` (con enriquecimiento) queda intacto.

Esta spec describe el comportamiento observable del sistema tras la implementación. No prescribe estructura interna.

---

## Requisitos

### Requirement: Detección del tipo de notificación MP y bifurcación de flujo

El sistema DEBE leer el campo `type` del payload RAW de MercadoPago para determinar el flujo a ejecutar.

- Si `type = "order"` → flujo sin enriquecimiento (esta spec).
- Si `type = "payment"` o si `type` está ausente → flujo con enriquecimiento (`/v1/payments`). Comportamiento previo sin cambios.

El sistema DEBE registrar en `SystemLogs` (categoría `Worker`) el tipo de notificación detectado al inicio del procesamiento, para diagnóstico.

#### Scenario: Detección de type=order activa flujo sin enriquecimiento

- GIVEN un `QueuedWebhook` de MercadoPago con payload RAW `{"type":"order","data":{"id":"ORD01KVX","external_reference":"003-CAJA_2__260624140146"}}`
- WHEN el worker procesa el webhook
- THEN el flujo NO llama a la API de MP (`GET /v1/payments/...`) y registra en `SystemLogs` el tipo detectado (`"order"`)

#### Scenario: Detección de type=payment conserva flujo con enriquecimiento (no regresión)

- GIVEN un `QueuedWebhook` de MercadoPago con payload RAW `{"type":"payment","data":{"id":"77777"}}`
- WHEN el worker procesa el webhook
- THEN el flujo llama a `GET /v1/payments/77777` para enriquecer y continúa el procesamiento habitual; el flujo `order` no interviene

#### Scenario: Notificación sin type conserva flujo con enriquecimiento (no regresión)

- GIVEN un `QueuedWebhook` de MercadoPago cuyo payload RAW no contiene el campo `type`
- WHEN el worker procesa el webhook
- THEN el flujo ejecuta enriquecimiento (`/v1/payments`) tal como lo hacía antes de este cambio

---

### Requirement: Extracción de routing key desde data.external_reference (flujo order)

Para notificaciones `type=order`, el sistema DEBE extraer la routing key leyendo `data.external_reference` del payload RAW (campo anidado bajo `data`, no en la raíz). El formato esperado es `{identificadorCaja}__{comprobante}` (separador `__`, doble guion bajo). El sistema DEBE aplicar la operación `external_reference.Split("__", 2)` y tomar la parte `[0]` como identificador de caja. Las mismas reglas de validación que el flujo `payment` aplican aquí.

#### Scenario: Extracción exitosa desde data.external_reference anidado

- GIVEN un payload RAW con `{"type":"order","data":{"id":"ORD01KVX","external_reference":"003-CAJA_2__260624140146"}}`
- WHEN el sistema extrae la routing key
- THEN el resultado es `"003-CAJA_2"` (porción anterior al primer `__`)

#### Scenario: external_reference con identificador que contiene guion bajo simple

- GIVEN un payload RAW con `data.external_reference = "CAJA_1__ORD-2024-001"` y una `CajaRegistrada` con `Identificador = "CAJA_1"`
- WHEN el sistema extrae la routing key
- THEN el resultado es `"CAJA_1"` y el lookup contra el padrón encuentra la caja exacta (comparación exacta, case-sensitive)

#### Scenario: Payload order sin data.external_reference produce dead-letter

- GIVEN un payload RAW con `{"type":"order","data":{"id":"ORD01KVX"}}` (sin `external_reference`)
- WHEN el sistema intenta extraer la routing key
- THEN el webhook va a dead-letter, se registra en `SystemLogs` con categoría `Worker`, y NO se reintenta

#### Scenario: external_reference sin separador __ produce dead-letter

- GIVEN un payload RAW con `data.external_reference = "003-CAJA_2-260624140146"` (sin `__`)
- WHEN el sistema intenta extraer la routing key
- THEN el webhook va a dead-letter, se registra en `SystemLogs` con categoría `Worker`, y NO se reintenta

#### Scenario: Parte izquierda vacía en external_reference produce dead-letter

- GIVEN un payload RAW con `data.external_reference = "__260624140146"` (identificador vacío antes del `__`)
- WHEN el sistema intenta extraer la routing key
- THEN el webhook va a dead-letter, se registra en `SystemLogs` con categoría `Worker`, y NO se reintenta

---

### Requirement: Ruteo directo — reenvío RAW firmado sin enriquecimiento (flujo order)

Para notificaciones `type=order`, una vez extraída la routing key válida, el sistema DEBE buscar la caja en el padrón del tenant por `(TenantId, identificadorCaja)` con comparación exacta (case-sensitive). Si la caja existe, el sistema DEBE reenviar el payload RAW original al `callbackUrl` de esa caja con el header `X-Caja-Signature` calculado como `HMAC-SHA256(payload_raw, WebhookSecret)` en hex lowercase. El sistema NO DEBE llamar a ninguna API externa de MercadoPago durante este flujo.

#### Scenario: Reenvío RAW exitoso a caja encontrada — flujo order

- GIVEN que `"003-CAJA_2"` existe en el padrón del tenant con `CallbackUrl = "https://caja2.cfargotunnel.com/webhook"` y WebhookSecret conocido
- AND un webhook `type=order` con `data.external_reference = "003-CAJA_2__260624140146"` y firma `x-signature` ya validada en el ingest
- WHEN el worker procesa el webhook
- THEN el sistema envía `POST https://caja2.cfargotunnel.com/webhook` con:
  - Body: el payload RAW exacto recibido de MercadoPago (sin modificaciones)
  - Header `X-Caja-Signature`: HMAC-SHA256(payload_raw, WebhookSecret) en hex lowercase
  - Header `X-Dotar-Gateway-ID`: slug del tenant
- AND el sistema NO llama a `GET /v1/payments/...` ni a ningún otro endpoint de MP

#### Scenario: Caja no encontrada en padrón produce dead-letter (flujo order)

- GIVEN que `"003-CAJA_INEXISTENTE"` NO existe en el padrón del tenant
- AND un webhook `type=order` con `data.external_reference = "003-CAJA_INEXISTENTE__260624140146"`
- WHEN el worker procesa el webhook
- THEN el webhook va a dead-letter, se registra en `SystemLogs` con categoría `Worker`, y NO se reintenta ni se reenvía a ninguna URL alternativa

---

### Requirement: No regresión del flujo type=payment

El flujo para notificaciones `type=payment` (o sin `type`) DEBE mantenerse idéntico al comportamiento previo: enriquecimiento con `GET /v1/payments/{id}`, extracción de `external_reference` desde el payload enriquecido (campo de raíz), y reenvío RAW firmado. Ningún cambio de este feature debe alterar ese flujo.

#### Scenario: Flujo payment completo sin cambios

- GIVEN un `QueuedWebhook` de MercadoPago con `{"type":"payment","data":{"id":"77777"}}` y una caja `"CAJA-01"` en el padrón con `external_reference = "CAJA-01__ORD-42"` en el objeto de pago enriquecido
- WHEN el worker procesa el webhook
- THEN el sistema llama a `GET /v1/payments/77777`, obtiene el payload enriquecido con `external_reference = "CAJA-01__ORD-42"`, extrae la routing key `"CAJA-01"`, y reenvía el payload RAW original a la `callbackUrl` de la caja con `X-Caja-Signature` correcto

---

### Requirement: Observabilidad — log de tipo de notificación

El sistema DEBE registrar en `SystemLogs` el tipo de notificación MercadoPago detectado (`"order"`, `"payment"`, o `"desconocido"` si ausente) para cada webhook procesado. El registro DEBE incluir al menos: identificador del QueuedWebhook, tipo detectado, y resultado del flujo (éxito / dead-letter con motivo).

#### Scenario: Log de tipo order registrado en SystemLogs

- GIVEN un webhook `type=order` procesado exitosamente
- WHEN el worker completa el flujo
- THEN existe un registro en `SystemLogs` con categoría `Worker` que indica tipo `"order"` y resultado exitoso para ese webhook

#### Scenario: Log de tipo order con dead-letter registrado en SystemLogs

- GIVEN un webhook `type=order` que termina en dead-letter (ej. caja no encontrada)
- WHEN el worker termina el procesamiento
- THEN existe un registro en `SystemLogs` con categoría `Worker` que indica tipo `"order"`, el motivo del dead-letter, y el identificador del webhook

---

## Restricciones del contrato (invariantes que no cambian)

| Elemento | Valor |
|----------|-------|
| Separador en `external_reference` | `__` (doble guion bajo) |
| Operación de extracción | `external_reference.Split("__", 2)[0]` |
| Header de firma al reenviar | `X-Caja-Signature` |
| Algoritmo de firma | HMAC-SHA256 |
| Codificación de la firma | Hex lowercase (sin prefijo) |
| Body reenviado | RAW de MercadoPago (nunca enriquecido) |
| Validación `x-signature` MP | Ya realizada en el endpoint de ingest; el worker no la repite |
| Secret usado en la firma de reenvío | `WebhookSecret` del tenant (mismo que el auto-registro) |

Referencia: `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md` (secciones B y C).

---

## Non-goals (fuera de alcance)

- Re-procesar dead-letters existentes de `type=order` en producción (ya perdidos, aceptado).
- Soporte para otros tipos de notificaciones MP distintos de `order` y `payment`.
- Cambiar el formato de `external_reference`, `X-Caja-Signature`, o la validación `x-signature` entrante.
- Refactor del flujo `payment` más allá de la bifurcación por tipo.
- Nueva UI o endpoints públicos.
