# auto-registro-caja-api — Especificación

## Propósito

Contrato público del endpoint de auto-registro de cajas. Define la forma exacta del request, el esquema de firma HMAC, las respuestas y las reglas de validación. Esta especificación es el entregable de boundary para el consumidor (ERP "DEAM Gestión").

---

## Requirements

### Requirement: Forma del request de registro

El sistema DEBE exponer `POST /registro-caja/{tenant-slug}` que acepta un cuerpo JSON con los campos `identificador` (string opaco, sin `::`) y `callbackUrl` (string, URL HTTPS). Ambos campos son OBLIGATORIOS. El endpoint DEBE retornar `400 Bad Request` si alguno está ausente o vacío.

#### Scenario: Request completo y válido

- GIVEN un cuerpo `{"identificador": "CAJA-01", "callbackUrl": "https://caja.ejemplo.com/cb"}` con HMAC válido
- WHEN se envía `POST /registro-caja/mi-tenant`
- THEN el endpoint procesa el registro y retorna `200 OK`

#### Scenario: Campo identificador ausente

- GIVEN un cuerpo `{"callbackUrl": "https://caja.ejemplo.com/cb"}` (sin `identificador`)
- WHEN se envía `POST /registro-caja/mi-tenant` con HMAC válido
- THEN el endpoint retorna `400 Bad Request` indicando campo faltante

#### Scenario: Tenant no encontrado

- GIVEN un slug `"tenant-inexistente"` que no corresponde a ningún tenant registrado
- WHEN se envía `POST /registro-caja/tenant-inexistente`
- THEN el endpoint retorna `404 Not Found`

---

### Requirement: Esquema HMAC del request de registro

El request de registro DEBE incluir el header `X-Caja-Signature` con el valor HMAC-SHA256 del cuerpo JSON en hex lowercase, usando el `WebhookSecret` del tenant como clave. El gateway DEBE verificar este HMAC antes de cualquier procesamiento de datos.

#### Scenario: Header HMAC correcto

- GIVEN un cuerpo JSON y su HMAC-SHA256 hex lowercase calculado con el `WebhookSecret` del tenant
- WHEN el header `X-Caja-Signature` contiene ese valor y se envía al endpoint
- THEN la firma es válida y el registro procede

#### Scenario: Header HMAC incorrecto

- GIVEN un header `X-Caja-Signature` con un valor que no corresponde al cuerpo ni al secret del tenant
- WHEN el endpoint verifica la firma
- THEN retorna `401 Unauthorized` antes de procesar el cuerpo

---

### Requirement: Formato del identificador de caja — string opaca sin `::`

El campo `identificador` DEBE ser una string no vacía que NO contenga la secuencia `::`. El gateway almacena y compara el identificador EXACTO sin sub-parsearlo (puede contener guiones u otros caracteres). El sistema DEBE rechazar identificadores vacíos o que contengan `::`.

#### Scenario: Identificador opaco con guiones es aceptado

- GIVEN `identificador = "CAJA-ESPECIAL-01"` (string con guiones, sin `::`)
- WHEN se procesa el request con HMAC válido
- THEN el registro se acepta y el identificador se almacena tal cual

#### Scenario: Identificador vacío es rechazado

- GIVEN `identificador = ""` (cadena vacía)
- WHEN se procesa el request con HMAC válido
- THEN el endpoint retorna `400 Bad Request` indicando identificador inválido

#### Scenario: Identificador con `::` es rechazado

- GIVEN `identificador = "CAJA::01"` (contiene el separador reservado)
- WHEN se procesa el request con HMAC válido
- THEN el endpoint retorna `400 Bad Request` indicando identificador inválido

---

### Requirement: Formato de external_reference — separador `::`

El campo `external_reference` que el proveedor (ERP) incluye al crear el pago DEBE tener el formato `{identificadorCaja}::{comprobante}`, donde `identificadorCaja` corresponde exactamente al `identificador` registrado en el padrón y NO contiene `::`. El gateway extrae la routing key tomando la porción anterior al primer `::`.

#### Scenario: external_reference con separador `::` produce routing key correcta

- GIVEN `external_reference = "CAJA-ESPECIAL-01::00001234"`
- WHEN el gateway extrae la routing key
- THEN el resultado es `"CAJA-ESPECIAL-01"` y el lookup coincide con la caja registrada con ese identificador

#### Scenario: external_reference sin `::` produce dead-letter

- GIVEN `external_reference = "CAJA-01-00001234"` (formato legado sin separador `::`)
- WHEN el gateway intenta extraer la routing key
- THEN el webhook va a dead-letter con log categoría `Forward`

---

### Requirement: Forma del payload reenviado a la caja

El sistema DEBE reenviar a la `callbackUrl` de la caja el payload RAW original del proveedor (para MP: `{"topic": "...", "id": "..."}`) sin modificación de contenido. El reenvío DEBE incluir el header `X-Caja-Signature` con el HMAC-SHA256 del cuerpo en hex lowercase, usando el `WebhookSecret` del tenant como clave (mismo esquema que el registro). El reenvío NO DEBE incluir el payload enriquecido.

#### Scenario: Payload reenviado es el RAW del proveedor

- GIVEN un webhook MP con payload `{"topic":"payment","id":"12345"}`
- WHEN el gateway reenvía a la caja tras el enriquecimiento y ruteo exitosos
- THEN el cuerpo del POST a la caja es exactamente `{"topic":"payment","id":"12345"}` sin campos adicionales

#### Scenario: Header de firma presente en el reenvío

- GIVEN que el gateway va a reenviar a `callbackUrl = "https://caja1.dotarsoluciones.com/cb"`
- WHEN el POST se envía a la caja
- THEN el request incluye `X-Caja-Signature` con el HMAC-SHA256 del body en hex lowercase firmado con el `WebhookSecret` del tenant

---

### Requirement: Respuestas del endpoint de registro

El sistema DEBE retornar los siguientes códigos HTTP según el resultado: `200 OK` en registro/refresco exitoso, `400 Bad Request` en validación fallida (campos ausentes, `identificador` vacío o con `::`, `callbackUrl` inválida/fuera de allowlist), `401 Unauthorized` en firma HMAC ausente o inválida, `404 Not Found` en tenant no encontrado, `429 Too Many Requests` en exceso de rate limit.

#### Scenario: Mapa de respuestas verificable

| Condición | Código esperado |
|-----------|-----------------|
| Registro exitoso | `200 OK` |
| Re-registro (idempotente) | `200 OK` |
| Campo obligatorio ausente | `400 Bad Request` |
| `identificador` vacío | `400 Bad Request` |
| `identificador` contiene `::` | `400 Bad Request` |
| `callbackUrl` sin `https://` | `400 Bad Request` |
| `callbackUrl` fuera de allowlist | `400 Bad Request` |
| `X-Caja-Signature` ausente | `401 Unauthorized` |
| `X-Caja-Signature` inválida | `401 Unauthorized` |
| Tenant no encontrado | `404 Not Found` |
| Rate limit excedido | `429 Too Many Requests` |

- GIVEN cada una de las condiciones de la tabla anterior
- WHEN se envía el request correspondiente a `POST /registro-caja/{tenant-slug}`
- THEN el endpoint retorna exactamente el código HTTP indicado
