# Contrato del boundary — Ruteo de webhooks multi-tenant (Gateway ↔ ERP DEAM Gestión)

**Versión**: 1.1  
**Estado**: Publicado  
**Propietario**: Dotar Gateway  
**Audiencia**: Equipo ERP DEAM Gestión  

---

## Introducción

El Gateway es el único punto de entrada para los webhooks de proveedores externos (MercadoPago). La responsabilidad de este documento es describir exactamente qué debe implementar el ERP (consumidor) para integrarse correctamente.

Este documento es autocontenido: un desarrollador del ERP debe poder implementar su lado leyendo únicamente este archivo.

---

## A. Auto-registro de caja

El ERP debe registrar cada caja ante el Gateway al arrancar (o al cambiar su URL de callback). El Gateway guarda la asociación `identificador → callbackUrl` y la usa para rutear webhooks entrantes.

### Endpoint

```
POST /registro-caja/{slug}
```

`{slug}` es el slug del tenant en el Gateway (lo provee el equipo de operaciones).

### Body JSON

```json
{
  "identificador": "<string opaco de caja>",
  "callbackUrl": "https://<tunel>.cfargotunnel.com/webhook"
}
```

| Campo | Tipo | Descripción |
|-------|------|-------------|
| `identificador` | string | Identificador opaco de la caja. Generado libremente por el ERP. Puede contener letras, números, guiones y guion bajo simple (ej. `003-CAJA_2`). **Restricción única: no puede contener `__`** (doble guion bajo, reservado como separador). Máximo 100 caracteres. |
| `callbackUrl` | string | URL de callback del ERP. Debe ser `https://` y coincidir con la allowlist del tenant (`*.cfargotunnel.com` o `*.dotarsoluciones.com`). |

### Firma de autenticación

El request debe incluir el header `X-Caja-Signature` con el valor calculado así:

```
X-Caja-Signature: HMAC-SHA256(body_raw, WebhookSecret)  →  hex lowercase
```

- `body_raw`: el body del request tal cual se envía (bytes UTF-8, sin modificaciones).
- `WebhookSecret`: el secret del tenant en el Gateway (lo provee el equipo de operaciones).
- Algoritmo: HMAC-SHA256.
- Codificación del resultado: **hex lowercase** (no base64).

Ejemplo en Python:
```python
import hmac, hashlib
secret = b"mi-webhook-secret"
body = b'{"identificador":"CAJA-01","callbackUrl":"https://tunel.cfargotunnel.com/webhook"}'
signature = hmac.new(secret, body, hashlib.sha256).hexdigest()
# signature es la cadena hex lowercase a enviar en X-Caja-Signature
```

### Comportamiento esperado

| Situación | Código HTTP | Descripción |
|-----------|-------------|-------------|
| Registro nuevo exitoso | `200 OK` | Caja registrada. |
| Re-registro (mismo identificador) | `200 OK` | Idempotente: actualiza `callbackUrl`. Sin duplicados. |
| Firma inválida o ausente | `401 Unauthorized` | No se persiste ningún dato. |
| `identificador` contiene `__` | `400 Bad Request` | Campo inválido. |
| `callbackUrl` con `http://` | `400 Bad Request` | Solo se acepta `https://`. |
| `callbackUrl` fuera de allowlist | `400 Bad Request` | Dominio no permitido. |
| Slug de tenant no encontrado | `404 Not Found` | Slug incorrecto. |

### Cuándo re-registrar

El ERP debe re-registrar la caja en los siguientes casos:
1. Al arrancar la aplicación (en caso de que el URL del túnel haya cambiado).
2. Cada vez que el túnel de Cloudflare cambie de URL.

---

## B. Payload reenviado a la caja

Cuando MercadoPago notifica al Gateway, el flujo varía según el campo `type` del payload:

### Flujo según tipo de notificación

| `type` | Descripción | Fuente de `external_reference` | Llamada a la API de MP |
|--------|-------------|-------------------------------|------------------------|
| `order` | Notificación Point / orden de pago | `data.external_reference` del payload RAW | **No** — ruteo directo sin enriquecimiento |
| `payment` (o ausente) | Notificación de pago estándar | campo raíz `external_reference` del payload enriquecido | Sí — GET `/v1/payments/{id}` |

**Flujo `type=order`** (sin enriquecimiento):
1. Valida la firma entrante de MercadoPago.
2. Lee `data.external_reference` del payload RAW (campo anidado dentro de `data`).
3. Extrae el `identificador` de caja con `Split("__", 2)[0]`.
4. Reenvía el payload **RAW** al `callbackUrl` registrado — sin ninguna llamada a la API de MP.

**Flujo `type=payment`** (con enriquecimiento):
1. Valida la firma entrante de MercadoPago.
2. Enriquece con la API de MP (GET `/v1/payments/{id}`).
3. Lee `external_reference` desde la raíz del payload enriquecido.
4. Extrae el `identificador` de caja con `Split("__", 2)[0]`.
5. Reenvía el payload **RAW** al `callbackUrl` registrado.

### Forma del reenvío (ambos flujos)

- **Body**: el payload RAW original de MercadoPago (no el payload enriquecido). Minimiza PII.  
  Ejemplo de payload entrante de MP (type=payment):
  ```json
  { "type": "payment", "data": { "id": "77777" } }
  ```
  Ejemplo de payload entrante de MP (type=order):
  ```json
  { "type": "order", "data": { "id": "ORD-01KVX5", "external_reference": "CAJA-01__260624140146" } }
  ```
- **Método HTTP**: `POST`.

### Headers incluidos en el reenvío

| Header | Valor | Descripción |
|--------|-------|-------------|
| `X-Caja-Signature` | HMAC-SHA256(body, WebhookSecret) en hex lowercase | Firma del Gateway para que la caja verifique autenticidad |
| `X-Dotar-Gateway-ID` | slug del tenant | Identificador del tenant emisor |

### Verificación de la firma en la caja

La caja debe verificar `X-Caja-Signature` con **el mismo algoritmo y el mismo secret** que usa para el auto-registro (sección A):

```
X-Caja-Signature: HMAC-SHA256(body_raw_recibido, WebhookSecret)  →  hex lowercase
```

Ejemplo en Python:
```python
import hmac, hashlib
secret = b"mi-webhook-secret"
body = request.body  # bytes del body recibido, sin modificar
expected = hmac.new(secret, body, hashlib.sha256).hexdigest()
received = request.headers["X-Caja-Signature"]
if not hmac.compare_digest(expected, received):
    return 401  # firma inválida
```

El secret es el mismo `WebhookSecret` del tenant para ambas direcciones: registro (A) y reenvío (B).

---

## C. Formato del identificador de caja

### Estructura

El identificador es **opaco** para el Gateway: una string no vacía que **no contiene `__`**.

Regex de validación: `^(?!.*__).+$`

El Gateway:
- Persiste el identificador tal cual lo registró el ERP.
- Compara **exactamente** (case-sensitive) contra `external_reference`.
- No interpreta el contenido (puede tener guiones, letras, números).

### Extracción desde `external_reference` de MercadoPago

Cuando el ERP genera una orden en MercadoPago, debe construir el `external_reference` así:

```
external_reference = "{identificadorCaja}__{comprobante}"
```

> El separador es `__` (doble guion bajo), no `::`. MercadoPago `/v1/orders` valida `external_reference` contra `^[A-Za-z0-9_-]{1,64}$` y rechaza `:`; `__` es admitido y el ERP garantiza que no aparece dentro de los campos.

| Parte | Descripción |
|-------|-------------|
| `{identificadorCaja}` | Exactamente el mismo `identificador` registrado en el Gateway (sección A). Sin modificaciones. Puede contener guion bajo simple. |
| `__` | Separador obligatorio. Solo debe aparecer una vez; el comprobante se sanitiza a `[A-Za-z0-9]`. |
| `{comprobante}` | Libre dentro de lo que admite MP. Número de orden, factura, o cualquier referencia interna del ERP. No lo usa el Gateway para rutear. |

Ejemplos válidos de `external_reference`:
- `CAJA-01__ORD-2024-001`
- `003-CAJA_2__260624095836`
- `C1__1`

Ejemplos **inválidos** (causan dead-letter en el Gateway):
- `CAJA-01` (sin `__` → Gateway no puede extraer el identificador)
- `__ORD-001` (parte izquierda vacía)
- `CAJA__01__ORD` (ambigüedad — se toma `CAJA` como identificador, `01__ORD` como comprobante, pero el identificador registrado debe ser exactamente `CAJA`)

### Procesamiento en el Gateway

El Gateway extrae la routing key con:
```
external_reference.Split("__", 2) → parte [0] = identificadorCaja
```

Si no hay `__` o la parte izquierda es vacía → el webhook va a dead-letter (se registra en logs, no se reintenta).

---

## D. Instrucciones para el consumidor (ERP DEAM Gestión)

Lista de pasos obligatorios para la integración:

### 1. Al inicializar cada caja

```
POST /registro-caja/{slug}
Header: X-Caja-Signature: <hmac-sha256-hex-lowercase>
Body: { "identificador": "<id-opaco>", "callbackUrl": "https://<tunel>/webhook" }
```

- El `identificador` debe ser estable (no cambiar entre reinicios).
- Re-registrar cada vez que el túnel cambie de URL.
- El re-registro es idempotente: responde 200 y actualiza la URL.

### 2. Al generar una orden en MercadoPago

- Fijar el campo `external_reference` de la preferencia de pago con el formato:
  ```
  external_reference = "{identificadorCaja}__{comprobante}"
  ```
- Usar **exactamente** el mismo `identificador` que se registró en el Gateway.

### 3. En el endpoint de callback (`callbackUrl`)

El ERP debe exponer un endpoint POST en la `callbackUrl` registrada que:

1. Lea el body RAW (sin parsear primero).
2. Verifique `X-Caja-Signature` con HMAC-SHA256 hex lowercase (sección B).
3. Si la firma es inválida → responder 401 (el Gateway no reintenta con firma inválida).
4. Si la firma es válida → procesar el payload (notificación de pago de MercadoPago).
5. Responder 2xx para confirmar recepción; respuestas 4xx/5xx activan el retry del Gateway.

### 4. Diagrama de flujo resumido

```
ERP (arranque)                          Gateway                    MercadoPago
     │                                      │                           │
     │── POST /registro-caja/{slug} ───────►│                           │
     │◄─ 200 OK ────────────────────────────│                           │
     │                                      │                           │
     │── Crear orden MP ──────────────────────────────────────────────►│
     │   external_reference = "CAJA-01__ORD-42"                        │
     │                                      │                           │
     │                                      │◄── POST /webhook/mp ─────│
     │                                      │    (notificación MP)      │
     │                                      │                           │
     │                                      │  [bifurcación por type]   │
     │                                      │                           │
     │                         type=payment:│── GET /v1/payments/... ──►│
     │                                      │◄─ { "external_reference": │
     │                                      │    "CAJA-01__ORD-42" } ───│
     │                                      │                           │
     │                          type=order: │  (sin llamada a MP API)   │
     │                                      │  lee data.external_reference│
     │                                      │  del payload RAW          │
     │                                      │                           │
     │◄── POST callbackUrl ─────────────────│                           │
     │    X-Caja-Signature: <hmac>          │                           │
     │    Body: payload RAW de MP           │                           │
     │── 200 OK ────────────────────────────►│                           │
```

### 5. Consideraciones de seguridad

- El `WebhookSecret` debe mantenerse confidencial; no incluirlo en logs ni en código fuente.
- Siempre verificar `X-Caja-Signature` antes de procesar el payload.
- La verificación debe ser **timing-safe** (usar `hmac.compare_digest` en Python, `CryptographicOperations.FixedTimeEquals` en .NET, etc.).
- El `callbackUrl` debe ser `https://` y estar en la allowlist del Gateway.

---

## Referencia rápida — Campos y algoritmos

| Elemento | Valor |
|----------|-------|
| Header de firma (ambas direcciones) | `X-Caja-Signature` |
| Algoritmo de firma | HMAC-SHA256 |
| Codificación del resultado | Hex lowercase (sin prefijo `sha256=`) |
| Separador en `external_reference` | `__` (doble guion bajo) |
| Cuerpo reenviado por el Gateway | RAW de MercadoPago (no enriquecido) |
| Secret usado en ambas direcciones | `WebhookSecret` del tenant |
