# caja-registrada-padron — Especificación

## Propósito

Padrón persistente de cajas POS por tenant. Cada entrada asocia un `identificador` opaco (string libre, sin `::`) con la `callbackUrl` del túnel de la caja. El padrón se puebla por auto-registro HMAC; el hot path de ruteo lo consulta mediante lookup exacto indexado.

---

## Requirements

### Requirement: Registro de caja autenticado

El sistema DEBE aceptar un request de auto-registro de caja únicamente si la firma HMAC incluida en el request es válida, usando el `WebhookSecret` del tenant como clave y el esquema `Generic` (SHA-256, hex lowercase, header `X-Caja-Signature`). Un request sin firma o con firma inválida DEBE ser rechazado con `401 Unauthorized`.

#### Scenario: Registro exitoso con HMAC válido

- GIVEN un tenant activo con `WebhookSecret` configurado
- WHEN se recibe `POST /registro-caja/{tenant-slug}` con `identificador`, `callbackUrl` y firma `X-Caja-Signature` válida
- THEN la caja se persiste en el padrón del tenant, `RegistradaEn` se asigna a UTC now y el endpoint retorna `200 OK`

#### Scenario: Rechazo por firma ausente

- GIVEN un tenant activo con `WebhookSecret` configurado
- WHEN se recibe `POST /registro-caja/{tenant-slug}` sin header `X-Caja-Signature`
- THEN el endpoint retorna `401 Unauthorized` y no se persiste ningún registro

#### Scenario: Rechazo por firma inválida

- GIVEN un tenant activo con `WebhookSecret` configurado
- WHEN se recibe `POST /registro-caja/{tenant-slug}` con `X-Caja-Signature` que no corresponde al cuerpo y secret del tenant
- THEN el endpoint retorna `401 Unauthorized` y no se persiste ningún registro

---

### Requirement: Idempotencia y refresco

El sistema DEBE tratar el re-registro de una caja ya existente (mismo `TenantId` + mismo `identificador`) como una actualización: DEBE sobrescribir la `callbackUrl` y actualizar el heartbeat (`UltimaVez` = UTC now y `ActualizadaEn` = UTC now). NO DEBE crear un registro duplicado.

#### Scenario: Re-registro actualiza callbackUrl

- GIVEN una `CajaRegistrada` con `identificador = "CAJA-01"` y `callbackUrl = "https://old.tunnel.com/callback"` ya persistida para el tenant
- WHEN se recibe un nuevo registro con el mismo `identificador` y `callbackUrl = "https://new.tunnel.com/callback"` con HMAC válido
- THEN la entrada existente es actualizada: `CallbackUrl = "https://new.tunnel.com/callback"`, `ActualizadaEn` refleja el momento UTC y no existe un segundo registro con ese `identificador`

#### Scenario: Re-registro sin cambio de URL refresca heartbeat

- GIVEN una `CajaRegistrada` con `identificador = "CAJA-01"` ya persistida
- WHEN se recibe un nuevo registro con idénticos `identificador` y `callbackUrl` con HMAC válido
- THEN `UltimaVez` se actualiza a UTC now y la operación retorna `200 OK`

---

### Requirement: Validación anti-SSRF de callbackUrl

El sistema DEBE rechazar con `400 Bad Request` toda `callbackUrl` que no cumpla simultáneamente: esquema `https://` y dominio incluido en la allowlist de túneles configurada en `appsettings.json`. DEBE rechazar URLs con esquema `http://`, `file://`, `ftp://` o cualquier otro esquema no permitido. El cliente HTTP usado para reenvíos a cajas DEBE tener `AllowAutoRedirect = false`.

#### Scenario: Rechazo por esquema http

- GIVEN un request de registro con `callbackUrl = "http://mi-caja.ejemplo.com/callback"`
- WHEN se procesa el registro con HMAC válido
- THEN el endpoint retorna `400 Bad Request` con mensaje de esquema inválido y no se persiste el registro

#### Scenario: Rechazo por dominio fuera de allowlist

- GIVEN una allowlist configurada con `["*.trycloudflare.com","*.dotarsoluciones.com"]`
- WHEN se recibe un registro con `callbackUrl = "https://atacante.com/callback"` con HMAC válido
- THEN el endpoint retorna `400 Bad Request` con mensaje de dominio no permitido

#### Scenario: Aceptación de URL válida en allowlist

- GIVEN una allowlist configurada con `["*.dotarsoluciones.com"]`
- WHEN se recibe un registro con `callbackUrl = "https://caja1.dotarsoluciones.com/callback"` con HMAC válido
- THEN el registro se persiste y la operación retorna `200 OK`

---

### Requirement: Heartbeat y TTL

El sistema DEBE registrar `UltimaVez` (UTC) en cada registro exitoso (nuevo o re-registro). Las cajas cuya `UltimaVez` supere el TTL configurado (global, en `appsettings.json`) DEBEN ser consideradas muertas por el hot path de ruteo: el lookup DEBE excluirlas como destino válido (misma semántica que "no encontrada" → dead-letter).

#### Scenario: Caja muerta excluida del ruteo

- GIVEN una `CajaRegistrada` con `UltimaVez` anterior al TTL configurado
- WHEN el worker intenta rutear a `identificador = "CAJA-01"`
- THEN el lookup no devuelve esa caja (equivalente a no encontrada) y el webhook va a dead-letter

#### Scenario: Caja viva incluida en el ruteo

- GIVEN una `CajaRegistrada` con `UltimaVez` dentro del TTL configurado
- WHEN el worker hace lookup por `(TenantId, "CAJA-01")`
- THEN la caja es devuelta con su `CallbackUrl` y el worker puede reenviar

---

### Requirement: Lookup por (tenant, identificador) — comparación exacta

El sistema DEBE exponer una operación de lookup indexada por `(TenantId, Identificador)` para uso del worker en el hot path. La comparación DEBE ser EXACTA (case-sensitive); el gateway NO sub-parsea el `Identificador`. La operación DEBE requerir que la caja esté dentro del TTL (no muerta) para retornar un resultado válido. DEBE retornar `null` / resultado vacío si no existe o está muerta.

#### Scenario: Lookup exitoso

- GIVEN una `CajaRegistrada` viva con `TenantId = 1` e `Identificador = "CAJA-ESPECIAL-01"`
- WHEN el worker llama al lookup con `(TenantId: 1, Identificador: "CAJA-ESPECIAL-01")`
- THEN el resultado contiene la `CallbackUrl` de esa caja

#### Scenario: Lookup sin resultado

- GIVEN que no existe ninguna `CajaRegistrada` con `TenantId = 1` e `Identificador = "CAJA-X99"`
- WHEN el worker llama al lookup con `(TenantId: 1, Identificador: "CAJA-X99")`
- THEN el resultado es nulo o vacío

---

### Requirement: Rate limiting en el endpoint de auto-registro

El sistema DEBE aplicar rate limiting en `POST /registro-caja/{tenant-slug}` para prevenir floods de registros. Requests que superen el límite configurado DEBEN recibir `429 Too Many Requests`.

#### Scenario: Rechazo por exceso de requests

- GIVEN que se han enviado más requests de registro del límite configurado en la ventana de tiempo
- WHEN llega un request adicional de registro para el mismo tenant
- THEN el endpoint retorna `429 Too Many Requests`
