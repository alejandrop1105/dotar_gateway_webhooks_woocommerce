# Proposal: Ruteo multi-tenant de webhooks entrantes con enriquecimiento por proveedor

## Intent

Hoy el gateway opera 1-a-1: cada tenant reenvía a un único `Tenant.TargetUrl` fijo. Las plataformas externas (MercadoPago primero) permiten configurar UNA sola URL de notificación, pero el sistema lo usan varios tenants × varias cajas POS. El webhook de MP trae `user_id` (→ tenant) e `id` (pago), pero NO identifica la caja destino. **Insight cerrado:** la identidad de la caja la pone quien origina la orden en el viaje de ida (`external_reference = {CodSucursal}-{IdCaja}-{comprobante}`); en la vuelta se BUSCA en un padrón persistente, no se deduce. El gateway debe enriquecer el pago contra la API del proveedor SOLO para extraer la routing key, ubicar la caja en el padrón del tenant y reenviarle el webhook RAW.

## Scope

### In Scope
- **Capacidad A** — Padrón de destinos por tenant (`CajaRegistrada`) con auto-registro HMAC idempotente/refrescable + heartbeat/TTL.
- **Capacidad B** — Config por tenant+proveedor (`ProveedorWebhookConfig`, credenciales cifradas) + regla del proveedor.
- Abstracción genérica `IWebhookProvider` (keyed DI .NET 9); MercadoPago como primer impl, cero semántica MP en el núcleo.
- Enriquecimiento + ruteo dinámico en el `WebhookDispatcherWorker` (no en el ingest).
- **API REST** de administración (config de proveedor por tenant) y endpoint público de auto-registro de cajas. SIN UI Blazor.
- **Contrato del boundary publicado** como entregable (ver Dependencies).

### Out of Scope
- UI Blazor de gestión del padrón / config de proveedor → follow-up posterior al deploy.
- Refresh automático de credenciales (OAuth refresh_token); token por config, si expira → error + log.
- Segundo proveedor real (abstracción lista, no implementada en v1).
- Polling de la caja (lo maneja el ERP, red de seguridad del lado ERP).

## Capabilities

### New Capabilities
- `caja-registrada-padron`: padrón de destinos por tenant, auto-registro HMAC, lookup en hot path, heartbeat/TTL.
- `proveedor-webhook-config`: config de enriquecimiento por tenant+proveedor con credenciales cifradas.
- `webhook-provider-routing`: abstracción `IWebhookProvider` (detectar, enriquecer, extraer routing key) + impl MercadoPago.
- `auto-registro-caja-api`: endpoint público de auto-registro firmado HMAC con defensa anti-SSRF.

### Modified Capabilities
- `tenant-application-service`: el despacho deja de usar `Tenant.TargetUrl` fijo; el worker resuelve el destino vía padrón. El reenvío 1-a-1 actual se preserva como fallback solo donde no aplica ruteo por proveedor.

## Approach

- **Modelo de datos:** 2 entidades nuevas + 2 migraciones EF Core. `CajaRegistrada (TenantId, Identificador {CodSucursal}-{IdCaja} UNIQUE, CallbackUrl, heartbeat)` — SIN secret propio: el `WebhookSecret` del tenant firma/valida ambas direcciones (auto-registro caja→gateway y reenvío gateway→caja); `ProveedorWebhookConfig (TenantId, ProveedorNombre, CredencialesJson cifrado, BaseUrl)`.
- **AppServices nuevos** siguen `TenantAppService` + `Result<T>`; cache-aside (modelo `ITenantCacheService`) para el lookup del padrón en el hot path.
- **Flujo:** ingest sigue igual (validar HMAC + encolar + 202; agrega `ProveedorNombre` al `QueuedWebhook`). El worker: resolver `IWebhookProvider` → enriquecer (`GET /v1/payments/{id}`) → extraer routing key → buscar caja en padrón → reenviar RAW a `callbackUrl` reusando cola Redis + CB Polly keyed por `callbackUrl`.
- **Dead-letter (decisión cerrada):** caja no encontrada o `external_reference` mal formado → dead-letter + log/métrica. NO reenvío, NO fallback a `TargetUrl`. Evaluar nuevo `DeliveryStatus` en design.
- **Payload reenviado (decisión cerrada):** RAW de MP (topic+id), firmado HMAC con el secret del tenant. El enriquecido NO se propaga (minimiza PII).
- **Seguridad (alto nivel):** anti-SSRF (`https://` + allowlist de dominios de túnel + `AllowAutoRedirect=false` + rate limit), credenciales cifradas con `IDataProtector`.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Domain/Entities/CajaRegistrada.cs`, `ProveedorWebhookConfig.cs` | New | Entidades del padrón y config |
| `Infrastructure/Data/GatewayDbContext.cs` + migraciones | Modified | 2 entidades + índices únicos |
| `Application/*AppService.cs` | New | CRUD padrón y config (`Result<T>`) |
| `Infrastructure/Services/ICajaRegistradaCacheService.cs` | New | Cache-aside hot path |
| `Providers/IWebhookProvider.cs` + `MercadoPagoProvider.cs` | New | Abstracción + impl MP (keyed DI) |
| `Endpoints/RegistroCajaEndpoints.cs` + admin | New | Auto-registro + config por API |
| `Workers/WebhookDispatcherWorker.cs` | Modified | Enriquecer → rutear → forward dinámico; CB keyed por `callbackUrl` |
| `Domain/QueuedWebhook` | Modified | Agregar `ProveedorNombre` |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| SSRF vía `CallbackUrl` registrada | High | `https://` + allowlist túnel + HMAC obligatorio + sin redirects + rate limit |
| Latencia del enriquecimiento en el worker baja throughput | Med | `HttpClient` dedicado por proveedor con timeout propio; no reusar `GatewayForwarder` 30s |
| Caja no encontrada / `external_reference` mal formado | Med | Dead-letter + log/métrica (decisión cerrada); ERP recupera por polling |
| Rotación de credenciales del proveedor (token expira) | Med | v1: error + log explícito; refresh automático es follow-up |
| Revocar una caja exige rotar el secret del tenant | Low | Tradeoff aceptado en v1; heartbeat/TTL limpia entradas muertas |

## Rollback Plan

Revertir el commit/PR de la feature. Las migraciones EF Core nuevas son aditivas (2 tablas, sin alterar `Tenant`): `dotnet ef migrations remove` o `Update-Database` al snapshot previo. El flujo 1-a-1 con `Tenant.TargetUrl` sigue intacto si la feature se desactiva. NO tocar `gateway.db` ni volúmenes; tenants productivos intactos.

## Dependencies

- **Contrato del boundary (entregable, el gateway lo OWNS, se define UNA vez y se publica):**
  1. Forma del request de auto-registro (campos `{CodSucursal}-{IdCaja}`, `callbackUrl`; firma HMAC: esquema + header).
  2. Esquema/algoritmo HMAC exacto del registro y del payload reenviado.
  3. Forma del payload reenviado al callback (RAW de MP + headers que lleva).
  4. Formato del identificador `{CodSucursal}-{IdCaja}` (debe coincidir con lo que la regla extrae de `external_reference`).
- **Consumidor:** ERP "DEAM Gestión" (engram `deam-gestion`, topic espejo `architecture/mp-gateway-multitenant-routing`) arranca su lado SOLO cuando el gateway termine y deploye.
- TDD estricto (xUnit, `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`).

## Success Criteria

- [ ] Una caja se auto-registra (HMAC válido) y persiste en el padrón del tenant; re-registro actualiza su `callbackUrl` (idempotente).
- [ ] Un webhook de MP se enriquece, extrae `{CodSucursal}-{IdCaja}` del `external_reference` y reenvía el RAW a la `callbackUrl` correcta.
- [ ] Caja no encontrada o `external_reference` mal formado → dead-letter + log/métrica, sin reenvío ni fallback.
- [ ] Registro con esquema/dominio inválido (anti-SSRF) es rechazado.
- [ ] Credenciales del proveedor se almacenan cifradas y se usan para enriquecer.
- [ ] El contrato del boundary queda documentado y publicado para el consumidor.
- [ ] Tenants productivos y el flujo 1-a-1 existente siguen funcionando sin regresión.
