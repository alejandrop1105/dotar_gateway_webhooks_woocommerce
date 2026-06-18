# API del Webhooks Gateway — Guía de integración

Esta guía describe cómo integrar un sistema externo con el **Webhooks Gateway**: cómo
gestionar tenants vía API REST y cómo enviar webhooks al endpoint de ingesta.

El Gateway recibe webhooks de un sistema origen (WooCommerce, GitHub, u otro), valida su
firma HMAC, los encola y los reenvía de forma confiable —con reintentos y circuit breaker—
a la URL de destino (`targetUrl`) de cada tenant.

---

## 1. Conceptos

| Concepto | Descripción |
|---|---|
| **Tenant** | Sistema origen registrado en el Gateway. Identificado por un `slug` único. |
| **slug** | Identificador en la URL de ingesta (`/ingest/{slug}`). Lowercase alfanumérico con guiones, 1–100 caracteres, sin guión inicial/final. |
| **targetUrl** | URL downstream a la que el Gateway reenvía los webhooks del tenant. |
| **webhookSecret** | Secreto compartido para validar la firma HMAC-SHA256 del webhook entrante. |
| **SignatureScheme** | Esquema de firma del sistema origen (ver §4). |

---

## 2. Autenticación

Todos los endpoints bajo `/api/tenants` requieren la **API Key del Gateway** en el header:

```
X-Gateway-Api-Key: <api-key>
```

- La API Key se autogenera al primer arranque (queda en el log de inicio) y puede rotarse
  desde el Dashboard → **Configuración**.
- Si falta o es incorrecta, la respuesta es `401 Unauthorized` sin cuerpo.
- La comparación es timing-safe.

> El endpoint de ingesta (`/ingest/{slug}`) **no** usa esta API Key: se autentica por firma
> HMAC (ver §6).

### Base URL

| Entorno | Base URL |
|---|---|
| Público (Cloudflare) | `https://webhook-gateway.dotarsoluciones.com` |
| Local | `http://localhost:8082` |

---

## 3. Endpoints de gestión de tenants

| Método | Ruta | Descripción |
|---|---|---|
| `POST` | `/api/tenants` | Crea un tenant nuevo |
| `GET` | `/api/tenants/{slug}` | Obtiene la configuración de un tenant |
| `PUT` | `/api/tenants/{slug}` | Actualiza un tenant (parcial) |
| `PUT` | `/api/tenants/{slug}/target-url` | Atajo para actualizar sólo la URL de destino |
| `DELETE` | `/api/tenants/{slug}` | Elimina el tenant y su histórico (cascada) |

---

### 3.1 Crear tenant — `POST /api/tenants`

**Body** (`application/json`):

| Campo | Tipo | Obligatorio | Default | Notas |
|---|---|---|---|---|
| `name` | string | ✅ | — | Nombre legible del tenant. |
| `slug` | string | ✅ | — | Se normaliza a lowercase. Debe cumplir el formato de slug. |
| `targetUrl` | string | ✅ | — | `http://` o `https://`. |
| `webhookSecret` | string | ❌ | autogenerado | Si se omite, se genera en base64 y se devuelve en la respuesta. Con `signatureScheme: "None"` queda vacío. |
| `signatureScheme` | string | ❌ | `"WooCommerce"` | Uno de: `WooCommerce`, `GitHub`, `Generic`, `None`. |
| `signatureHeader` | string | ❌ | header del esquema | Override del header donde llega la firma (útil con `Generic`). |
| `isActive` | bool | ❌ | `true` | Si `false`, la ingesta rechaza con `401`. |
| `retryPolicyId` | int | ❌ | null | Debe existir; si null usa la del grupo o la default. |
| `tenantGroupId` | int | ❌ | null | Debe existir. |

**Ejemplo (curl):**

```bash
curl -X POST "https://webhook-gateway.dotarsoluciones.com/api/tenants" \
  -H "X-Gateway-Api-Key: $GATEWAY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Mi Cliente",
    "slug": "mi-cliente",
    "targetUrl": "https://erp.cliente.com/webhooks/woo",
    "signatureScheme": "WooCommerce"
  }'
```

**Ejemplo (PowerShell):**

```powershell
$headers = @{ "X-Gateway-Api-Key" = $env:GATEWAY_API_KEY }
$body = @{
    name           = "Mi Cliente"
    slug           = "mi-cliente"
    targetUrl      = "https://erp.cliente.com/webhooks/woo"
    signatureScheme = "WooCommerce"
} | ConvertTo-Json
Invoke-RestMethod -Method Post `
    -Uri "https://webhook-gateway.dotarsoluciones.com/api/tenants" `
    -Headers $headers -ContentType "application/json" -Body $body
```

**Respuesta `201 Created`:**

```json
{
  "slug": "mi-cliente",
  "name": "Mi Cliente",
  "targetUrl": "https://erp.cliente.com/webhooks/woo",
  "webhookSecret": "8Jq...base64...=",
  "signatureScheme": "WooCommerce",
  "signatureHeader": null,
  "isActive": true,
  "retryPolicyId": null,
  "tenantGroupId": null,
  "createdAt": "2026-06-01T12:00:00Z"
}
```

> ⚠️ El `webhookSecret` sólo se devuelve en esta respuesta de creación. Guardalo de forma
> segura: lo necesitás para firmar los webhooks (o configurarlo en el sistema origen).

**Errores:** `400` (validación), `401` (API Key), `409` (slug ya en uso).

---

### 3.2 Obtener tenant — `GET /api/tenants/{slug}`

```bash
curl "https://webhook-gateway.dotarsoluciones.com/api/tenants/mi-cliente" \
  -H "X-Gateway-Api-Key: $GATEWAY_API_KEY"
```

**Respuesta `200 OK`** (no incluye el `webhookSecret`):

```json
{
  "slug": "mi-cliente",
  "name": "Mi Cliente",
  "targetUrl": "https://erp.cliente.com/webhooks/woo",
  "signatureScheme": "WooCommerce",
  "signatureHeader": null,
  "isActive": true,
  "createdAt": "2026-06-01T12:00:00Z",
  "updatedAt": null
}
```

**Errores:** `401`, `404`.

---

### 3.3 Actualizar tenant — `PUT /api/tenants/{slug}`

Actualización **parcial**: sólo se modifican los campos presentes en el body. Los campos
omitidos (o `null`) se dejan como están.

| Campo | Tipo | Notas |
|---|---|---|
| `name` | string | No puede quedar vacío. |
| `targetUrl` | string | `http://` o `https://`. |
| `webhookSecret` | string | Reemplaza el secreto actual. |
| `signatureScheme` | string | `WooCommerce`, `GitHub`, `Generic`, `None`. |
| `signatureHeader` | string | `""` (vacío) → vuelve al header default del esquema. |
| `isActive` | bool | Activa/desactiva la ingesta del tenant. |
| `retryPolicyId` | int | `0` → desasocia; `>0` → debe existir. |
| `tenantGroupId` | int | `0` → desasocia; `>0` → debe existir. |

**Convenciones de limpieza:** como un campo `null` significa "no cambiar", para *limpiar*
un campo opcional se usan sentinelas: `""` para `signatureHeader` y `0` para
`retryPolicyId` / `tenantGroupId`.

**Ejemplo — cambiar destino y rotar secreto:**

```bash
curl -X PUT "https://webhook-gateway.dotarsoluciones.com/api/tenants/mi-cliente" \
  -H "X-Gateway-Api-Key: $GATEWAY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "targetUrl": "https://erp.cliente.com/v2/webhooks",
    "webhookSecret": "nuevo-secreto-base64",
    "isActive": true
  }'
```

**Respuesta `200 OK`:** el tenant actualizado (mismo formato que el GET, más
`retryPolicyId` y `tenantGroupId`).

**Errores:** `400`, `401`, `404`.

---

### 3.4 Actualizar sólo la URL de destino — `PUT /api/tenants/{slug}/target-url`

Atajo cuando sólo se cambia el destino.

```bash
curl -X PUT "https://webhook-gateway.dotarsoluciones.com/api/tenants/mi-cliente/target-url" \
  -H "X-Gateway-Api-Key: $GATEWAY_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{ "targetUrl": "https://erp.cliente.com/v2/webhooks" }'
```

**Respuesta `200 OK`:**

```json
{
  "slug": "mi-cliente",
  "name": "Mi Cliente",
  "targetUrl": "https://erp.cliente.com/v2/webhooks",
  "previousUrl": "https://erp.cliente.com/webhooks/woo",
  "updatedAt": "2026-06-01T13:00:00Z"
}
```

---

### 3.5 Eliminar tenant — `DELETE /api/tenants/{slug}`

Borrado físico. Elimina en cascada los `DeliveryLogs` y `DeliveryAttempts` del tenant.

```bash
curl -X DELETE "https://webhook-gateway.dotarsoluciones.com/api/tenants/mi-cliente" \
  -H "X-Gateway-Api-Key: $GATEWAY_API_KEY"
```

**Respuesta `204 No Content`.** **Errores:** `401`, `404`.

---

## 4. Esquemas de firma (`SignatureScheme`)

El Gateway computa `HMAC-SHA256(webhookSecret, body)` y lo compara (timing-safe) contra la
firma que llega en el header. El header esperado y el formato dependen del esquema:

| Esquema | Header default | Formato de la firma |
|---|---|---|
| `WooCommerce` | `X-WC-Webhook-Signature` | base64 del HMAC |
| `GitHub` | `X-Hub-Signature-256` | `sha256=<hex lowercase>` |
| `Generic` | `X-Webhook-Signature` | hex lowercase del HMAC |
| `None` | — | sin validación (no recomendado) |

- `signatureHeader` permite sobrescribir el header (principalmente para `Generic`).
- El secreto se trata como los **bytes UTF-8 del string literal**: ambos lados deben usar
  exactamente el mismo string (sea base64, hex u otro).

---

## 5. Ciclo de integración recomendado

1. **Crear el tenant** (`POST /api/tenants`) con el `signatureScheme` del sistema origen.
   Guardar el `webhookSecret` devuelto.
2. **Configurar el sistema origen** para enviar webhooks a
   `https://webhook-gateway.dotarsoluciones.com/ingest/{slug}` usando ese secreto.
3. **Probar** enviando un webhook y verificando en `/monitor` y `/logs` del Dashboard que se
   recibió, validó y reenvió correctamente.
4. **Actualizar** el destino o el secreto con `PUT /api/tenants/{slug}` cuando haga falta.

---

## 6. Enviar webhooks — `POST /ingest/{slug}`

Endpoint público de ingesta. **No** usa la API Key; se autentica por firma HMAC según el
`signatureScheme` del tenant.

```
POST /ingest/{slug}
Content-Type: application/json
X-WC-Webhook-Signature: <base64(HMAC-SHA256(body, secret))>   # según esquema

<cuerpo crudo del webhook>
```

- El Gateway valida la firma sobre el **body crudo** (sin reserializar), encola y responde
  `202 Accepted`.
- Respuestas: `202` (aceptado y encolado), `401` (tenant inexistente/inactivo, body vacío o
  firma inválida).
- Los headers `X-*` del provider se propagan **verbatim** al downstream.

**Ejemplo de firma del body (esquema WooCommerce, PowerShell):**

```powershell
$secret = "mi-webhook-secret"
$body   = '{"id":123,"status":"completed"}'
$hmac   = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
$sig    = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)))

Invoke-RestMethod -Method Post `
    -Uri "https://webhook-gateway.dotarsoluciones.com/ingest/mi-cliente" `
    -ContentType "application/json" `
    -Headers @{ "X-WC-Webhook-Signature" = $sig } `
    -Body $body
```

**Health check:** `GET /health` → `{ "status": "healthy", "timestamp": "..." }` (sin auth).

### 6.1 Ping de diagnóstico — `/ingest/__ping`

Slug **reservado** para verificar que el Gateway recibe llamadas desde afuera. A diferencia
de `/health`, esta ruta entra al pipeline de ingesta: confirma DNS público → Cloudflare →
tunel → Gateway, y deja registro en `SystemLogs` (categoría `Ingest`).

- Acepta `GET` y `POST`. **No** valida firma, **no** busca tenant, **no** encola, **no**
  reenvía a ningún downstream.
- El `SlugRegex` de tenants rechaza el underscore, así que `__ping` no puede colisionar con
  un slug productivo.

```bash
curl https://webhook-gateway.dotarsoluciones.com/ingest/__ping
```

**Respuesta `200 OK`:**

```json
{
  "status": "ok",
  "slug": "__ping",
  "method": "GET",
  "timestamp": "2026-06-03T12:00:00Z"
}
```

---

## 7. Resumen de códigos de estado

| Código | Significado |
|---|---|
| `200 OK` | Operación de lectura/actualización exitosa. |
| `201 Created` | Tenant creado. |
| `202 Accepted` | Webhook aceptado y encolado. |
| `204 No Content` | Tenant eliminado. |
| `400 Bad Request` | Error de validación (cuerpo con `{ "error": "..." }`). |
| `401 Unauthorized` | API Key faltante/incorrecta, o firma HMAC inválida en ingesta. |
| `404 Not Found` | Tenant inexistente. |
| `409 Conflict` | El slug ya está en uso. |
