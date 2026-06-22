# Design: Ruteo multi-tenant de webhooks con enriquecimiento por proveedor

## Technical Approach

Se introduce una capa de **ruteo dinámico** entre la cola Redis y el `ForwardingService`, activada SOLO para tenants con `ProveedorWebhookConfig`. Los webhooks de proveedor entran por una **URL única sin slug** (`POST /webhook/{proveedor}`, p.ej. `/webhook/mercadopago`): el PROVEEDOR se resuelve por la RUTA y el TENANT se resuelve por la **cuenta externa del payload** (MP: `user_id`), no por slug. Ese endpoint nuevo valida la firma ENTRANTE (rechazo temprano 401) antes de encolar; el `WebhookDispatcherWorker` enriquece contra la API remota, extrae la routing key, busca la caja en el padrón y reenvía el RAW a la `CallbackUrl` con CB keyed por URL. El flujo 1-a-1 actual (`POST /ingest/{slug}` → HMAC con `Tenant.WebhookSecret` → `Tenant.TargetUrl`) **queda intacto: `IngestEndpoints` NO se toca** (cero regresión WooCommerce). Dos entidades nuevas (`CajaRegistrada`, `ProveedorWebhookConfig`), abstracción `IWebhookProvider` con keyed DI .NET 9, y `MercadoPagoProvider` como primer impl. Sigue el layout layered existente (`Domain/Entities`, `Application`, `Infrastructure/Services`, `Endpoints`, `Workers`) y el patrón `AppService` + `Result<T>` (Scoped) más cache-aside (modelo `ITenantCacheService`).

## Architecture Decisions

### Decision 1 — `IWebhookProvider` (abstracción central)

**Choice**: Interfaz genérica registrada con `AddKeyedSingleton<IWebhookProvider, MercadoPagoProvider>("mercadopago")`. Cero semántica MP en el núcleo. La resolución del proveedor por webhook entrante es **por la RUTA** (`/webhook/{proveedor}`): el endpoint nuevo resuelve `IServiceProvider.GetKeyedService<IWebhookProvider>(proveedor)` (404 si la key no existe). El TENANT NO sale del slug: se resuelve con `ResolverCuentaExterna(headers, body)` → `CuentaExternaId` → lookup inverso `ProveedorWebhookConfig (ProveedorNombre, CuentaExternaId)`. Un proveedor por tenant en v1; la abstracción soporta N.

```csharp
namespace Dotar.Gateway.Providers;

/// Resultado tipado de la extracción de routing key (sin excepciones de negocio).
public readonly record struct RoutingKeyResult(bool IsValid, string? Identificador, string? Motivo)
{
    public static RoutingKeyResult Ok(string id) => new(true, id, null);
    public static RoutingKeyResult Invalid(string motivo) => new(false, null, motivo);
}

public sealed record EnrichmentResult(bool IsSuccess, string? RawPayload, int? StatusCode, string? Error);

public interface IWebhookProvider
{
    /// (i) Nombre/clave del proveedor; debe coincidir con la key de DI, el segmento de ruta y ProveedorNombre.
    string Nombre { get; }

    /// (ii) Resuelve el identificador de CUENTA EXTERNA del payload entrante (MP: user_id).
    /// Devuelve null si no puede extraerlo (→ el endpoint responde 404/rechazo).
    string? ResolverCuentaExterna(IHeaderDictionary headers, byte[] body);

    /// (iii) Valida la firma del webhook ENTRANTE (MP: header x-signature + ts; secret en config).
    bool ValidarFirmaEntrante(IHeaderDictionary headers, byte[] body, ProveedorWebhookConfig config);

    /// (iv) Enriquece contra la API remota: id del evento → payload enriquecido (JSON crudo del proveedor).
    Task<EnrichmentResult> EnriquecerAsync(string idEvento, ProveedorWebhookConfig config, CancellationToken ct);

    /// (v) Extrae la routing key (identificador OPACO de caja) del payload enriquecido.
    RoutingKeyResult ExtraerRoutingKey(string payloadEnriquecido);
}
```

`MercadoPagoProvider` (primer impl): `Nombre = "mercadopago"`; `ResolverCuentaExterna` lee el `user_id` del payload entrante de MP (collector/vendedor que identifica al tenant); `ValidarFirmaEntrante` parsea `x-signature` (`ts=...,v1=...`) y valida HMAC-SHA256 con el secret de MP de `ProveedorWebhookConfig`; `EnriquecerAsync` hace `GET {BaseUrl}/v1/payments/{idEvento}` con `Authorization: Bearer {token}`; `ExtraerRoutingKey` lee `external_reference`, hace `Split("::", 2)` y devuelve la parte [0] (identificador OPACO de caja, tal cual lo generó el ERP — puede contener guiones); si no hay `::` o la parte izquierda es vacía → `RoutingKeyResult.Invalid`.

**Alternatives considered**: enum + switch (no extensible, semántica MP filtrada al núcleo); resolución por header del payload (acopla detección al formato MP).
**Rationale**: keyed DI es idiomático en .NET 9 y mantiene el núcleo agnóstico; `Result`-style structs evitan excepciones en el hot path y son trivialmente mockeables.

### Decision 2 — Modelo de datos

```csharp
namespace Dotar.Gateway.Domain.Entities;

public class CajaRegistrada            // SIN secret propio
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string Identificador { get; set; } = string.Empty; // OPACO: string que el ERP genera; comparación EXACTA (puede contener guiones, no "::")
    public string CallbackUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime UltimaVez { get; set; } = DateTime.UtcNow;   // heartbeat
}

public class ProveedorWebhookConfig
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string ProveedorNombre { get; set; } = string.Empty;  // "mercadopago"
    public string CuentaExternaId { get; set; } = string.Empty;  // user_id/cuenta del tenant en el proveedor (lookup inverso)
    public string CredencialesCifradas { get; set; } = string.Empty; // JSON cifrado (token, signingSecret)
    public string BaseUrl { get; set; } = string.Empty;          // "https://api.mercadopago.com"
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

EF en `GatewayDbContext.OnModelCreating`: `HasIndex(c => new { c.TenantId, c.Identificador }).IsUnique()`; `HasIndex(p => new { p.TenantId, p.ProveedorNombre }).IsUnique()`; **`HasIndex(p => new { p.ProveedorNombre, p.CuentaExternaId }).IsUnique()`** (lookup inverso desde el webhook entrante — global, no por tenant); ambas FK `OnDelete(Cascade)`; `Identificador` `HasMaxLength(100)`, `CuentaExternaId` `HasMaxLength(100)`, `CallbackUrl` `HasMaxLength(2000)`, `CredencialesCifradas` `HasColumnType("TEXT")`. `DbSet`s nuevos. Migraciones aditivas (no tocan `Tenant`): `dotnet ef migrations add AddCajaRegistrada` y `AddProveedorWebhookConfig` (o una sola `AddRuteoMultitenant`).

**Rationale**: índices únicos garantizan idempotencia del auto-registro (upsert por `(TenantId, Identificador)`), un único proveedor por tenant en v1, y un lookup inverso O(1) `(ProveedorNombre, CuentaExternaId) → tenant` para resolver el tenant del webhook entrante sin slug.

### Decision 3 — Cifrado de credenciales

**Choice**: `IDataProtector` con purpose `"ProveedorWebhookConfig.Credenciales.v1"`. Cifra/descifra en el **AppService** (`ProveedorWebhookConfigAppService`), no en la entidad: la entidad guarda solo el ciphertext (`CredencialesCifradas`), el AppService expone DTOs en claro. Reusa el `AddDataProtection()` ya configurado (keys en `/app/data/dataprotection-keys`).
**Alternatives considered**: cifrar en la entidad (mete infraestructura en `Domain`); columna en claro (inaceptable).
**Rationale**: mantiene `Domain` puro y centraliza el secreto en la capa de aplicación, igual que `TenantAppService` centraliza la generación de secrets.

### Decision 4 — Dead-letter

**Choice**: nuevo valor `DeadLetter` en el enum `DeliveryStatus` (persistido por `HasConversion<string>()`, NO es `[Flags]`, agregado es aditivo). Caja no encontrada o `external_reference` mal formado → `DeliveryLog` con `Status = DeliveryStatus.DeadLetter`, `NextRetryAt = null` (nunca entra al retry scheduler). Log `SystemLogCategory.Worker` nivel `Warn` con `details = "motivo=caja_no_encontrada|external_reference_invalido; routingKey=..."`.
**Alternatives considered**: tabla separada (duplica esquema de logs, complica el `/monitor`); reusar `Failed` (lo recogería el scheduler de reintentos → reenvío indebido).
**Rationale**: terminal, sin reintento, visible en el dashboard existente; mínima cirugía sobre el modelo de logs.

### Decision 5 — Endpoint de proveedor separado + flujo en el worker

**Choice**: El flujo de proveedor vive en un **endpoint NUEVO e independiente** del `/ingest/{slug}`: `POST /webhook/{proveedor}` (`WebhookProveedorEndpoints.cs`). `IngestEndpoints` **NO se modifica** — el flujo 1-a-1 (HMAC con `Tenant.WebhookSecret` → `Tenant.TargetUrl`) queda byte-a-byte como hoy. La firma ENTRANTE del proveedor se valida en este endpoint nuevo (rechazo temprano 401), no en el worker.

**Flujo del endpoint nuevo** (`HandleWebhookProveedor`):
1. Recibir webhook en `/webhook/{proveedor}` (sin slug de tenant) + leer body crudo.
2. Resolver `IWebhookProvider` por la RUTA: `GetKeyedService<IWebhookProvider>(proveedor)`; si no existe la key → `404` + log.
3. `ResolverCuentaExterna(headers, body)` → `cuentaExternaId`; si `null`/vacío → `404`/rechazo + log.
4. Buscar `ProveedorWebhookConfig` por `(ProveedorNombre = proveedor, CuentaExternaId)` (vía AppService/cache); si no matchea → `404`/rechazo + log (cuenta no provisionada).
5. De la config sale el **TENANT** (`TenantId`) y el secret de MP → `ValidarFirmaEntrante(headers, body, config)`; si falla → `401` + log `Auth`.
6. Encolar `QueuedWebhook` con `TenantId` + `ProveedorNombre` → responder `202`.

El worker (`ProcessNewWebhookAsync`) hace SOLO ruteo (igual que antes): resuelve `IWebhookProvider` por `webhook.ProveedorNombre` → `EnriquecerAsync(idEvento)` → `ExtraerRoutingKey` → lookup en `ICajaRegistradaCacheService.GetByIdentificadorAsync(tenantId, key)` → si hay caja, `ForwardAsync(caja.CallbackUrl, rawPayload, slug, headers)`; si no, dead-letter. El camino 1-a-1 sigue llegando por `/ingest/{slug}` y se reenvía a `webhook.TargetUrl` sin cambios. `ForwardAsync` ya recibe `targetUrl` como primer parámetro (URL dinámica soportada).

CB Polly: `ConcurrentDictionary<int, ...>` pasa a `ConcurrentDictionary<string, ResiliencePipeline<ForwardResult>>` keyed por `callbackUrl`. **Riesgo de fuga**: cada URL distinta crea un pipeline que vive mientras el worker (singleton) viva. **Mitigación**: cap del cache (p.ej. 500 entradas) con evicción LRU o, más simple, `MemoryCache` con TTL deslizante de 30 min por key — al expirar se reconstruye. Se documenta el cap como constante configurable.

**Rationale**: separar el endpoint elimina toda regresión sobre WooCommerce (`/ingest/{slug}` no se toca); resolver tenant por payload (no por slug) es fiel al brief (una sola URL pública por proveedor); validar la firma antes de encolar evita gastar enriquecimiento en payloads no autenticados y devuelve 401 a MP sin contaminar la cola.

### Decision 6 — HttpClients dedicados

**Choice**: dos clientes nombrados en `IHttpClientFactory` además de `GatewayForwarder` (30s):
- `"ProviderEnrichment"`: `Timeout = TimeSpan.FromSeconds(10)` (configurable por proveedor vía appsettings); lo usa `MercadoPagoProvider.EnriquecerAsync`.
- `"CajaCallback"`: `AllowAutoRedirect = false` vía `ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })`; lo usa el reenvío a cajas (el `ForwardingService` selecciona el cliente por nombre según haya proveedor o no).
**Rationale**: aísla el timeout de enriquecimiento del de forward; `AllowAutoRedirect=false` cierra open-redirect en el callback (anti-SSRF en runtime).

### Decision 7 — Anti-SSRF + rate limiting

**Choice**: validación de `CallbackUrl` en `CajaRegistradaAppService.RegistrarAsync`: (a) `https://` obligatorio; (b) allowlist de dominios desde `appsettings` (`Seguridad:CallbackDominiosPermitidos`, p.ej. sufijos `*.cfargotunnel.com`, `*.dotarsoluciones.com`); rechazo → `Result.Validation`. Runtime: `AllowAutoRedirect=false` (Decisión 6). Rate limiting del endpoint de auto-registro vía `AddRateLimiter` (fixed window por IP, alto nivel: 10 req/min).
**Rationale**: defensa en capas — validación en registro + sin redirects en forward + HMAC obligatorio + rate limit.

### Decision 8 — AppServices + cache

**Choice**: `CajaRegistradaAppService` y `ProveedorWebhookConfigAppService`, ambos `Scoped`, `Result<T>`, dependen de `GatewayDbContext`. `ICajaRegistradaCacheService` (Singleton, cache-aside con `IMemoryCache`, modelo `ITenantCacheService`) para el lookup en hot path:

```csharp
public interface ICajaRegistradaCacheService
{
    Task<CajaRegistrada?> GetByIdentificadorAsync(int tenantId, string identificador);
    void Invalidate(int tenantId, string identificador);
}
```

Tensión Scoped/Singleton: igual que `TenantCacheService`, el cache (Singleton) abre scope vía `IServiceScopeFactory` para resolver `GatewayDbContext` (Scoped) en el miss. El AppService llama `Invalidate` tras registrar/actualizar.
**Rationale**: el lookup del padrón está en el hot path del worker; cache-aside evita golpear SQLite por cada webhook.

### Decision 9 — Contrato del boundary

Ver sección "Contrato publicado" abajo (entregable que el gateway OWNS).

### Decision 10 — Estructura de carpetas/namespaces

**Choice**: `Providers/` en la raíz del proyecto (`Dotar.Gateway.Providers`) para `IWebhookProvider` + `MercadoPagoProvider` + DTOs (`RoutingKeyResult`, `EnrichmentResult`). Entidades en `Domain/Entities/`. AppServices en `Application/`. Cache en `Infrastructure/Services/`. Endpoints en `Endpoints/` (`WebhookProveedorEndpoints.cs`, `RegistroCajaEndpoints.cs`, `ProveedorConfigApiEndpoints.cs`); `IngestEndpoints.cs` no se toca.
**Rationale**: coherente con el layout layered existente; `Providers/` como módulo de extensión separado señala que es el punto de inserción de nuevos proveedores.

## Data Flow

```
FLUJO 1-a-1 (WooCommerce, SIN CAMBIOS — IngestEndpoints intacto):
POST /ingest/{slug} ──► lookup tenant ──► validar HMAC (Tenant.WebhookSecret)
                ──► EnqueueAsync(QueuedWebhook, TargetUrl, ProveedorNombre=null) ──► 202

FLUJO PROVEEDOR (endpoint NUEVO, WebhookProveedorEndpoints, Scoped DI):
POST /webhook/{proveedor}   (URL única, SIN slug)
        │
        ├─► resolver IWebhookProvider por la RUTA (keyed)   │ key inexistente → 404
        ├─► ResolverCuentaExterna(headers, body) → user_id  │ null/vacío → 404
        ├─► lookup ProveedorWebhookConfig (ProveedorNombre, CuentaExternaId)
        │        └─► de aquí sale TENANT + secret MP        │ sin match → 404 (no provisionado)
        ├─► ValidarFirmaEntrante(headers, body, cfg)        │ inválida → 401 (log Auth)
        └─► EnqueueAsync(QueuedWebhook + TenantId + ProveedorNombre) ──► 202
                                                          │
                                              ┌─────── Redis Queue ───────┐
                                                          │
                          ┌──────────── WORKER (singleton + scope on demand) ───────────┐
                  DequeueAsync ──► ¿ProveedorNombre?
                          │ NO  ──► ForwardAsync(TargetUrl)            [1-a-1, GatewayForwarder]
                          │ SI:
                          │   resolver IWebhookProvider (keyed)
                          │   EnriquecerAsync(idEvento)               [HttpClient "ProviderEnrichment" 10s]
                          │   ExtraerRoutingKey → external_reference.Split("::",2)[0]  (id OPACO)
                          │       │ inválida ──► DeadLetter + log Worker
                          │   GetByIdentificadorAsync(tenantId,key)   [ICajaRegistradaCacheService]
                          │       │ no encontrada ──► DeadLetter + log Worker
                          │   ForwardAsync(caja.CallbackUrl, RAW)     [HttpClient "CajaCallback" no-redirect]
                          │       └── CB Polly keyed por callbackUrl (cap/TTL)
                          └──► SaveDeliveryLog (Success | Scheduled | DeadLetter)

AUTO-REGISTRO:  POST /registro-caja/{slug} ──► validar HMAC (Tenant.WebhookSecret) + anti-SSRF (https+allowlist)
                ──► upsert CajaRegistrada (TenantId, Identificador) ──► Invalidate cache ──► 200
```

DI lifetimes: `IWebhookProvider` impls = **Singleton** (keyed, stateless, usan `IHttpClientFactory`); AppServices = **Scoped**; `ICajaRegistradaCacheService` = **Singleton**; `WebhookDispatcherWorker` = **Singleton**.

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Domain/Entities/CajaRegistrada.cs` | Create | Entidad padrón (sin secret) |
| `Domain/Entities/ProveedorWebhookConfig.cs` | Create | Config proveedor (credenciales cifradas) |
| `Domain/Entities/DeliveryLog.cs` | Modify | Agregar `DeadLetter` a `DeliveryStatus` |
| `Domain/Models/QueuedWebhook.cs` | Modify | Agregar `string? ProveedorNombre` |
| `Infrastructure/Data/GatewayDbContext.cs` | Modify | DbSets + índices únicos + 2 migraciones |
| `Providers/IWebhookProvider.cs` | Create | Abstracción + DTOs |
| `Providers/MercadoPagoProvider.cs` | Create | Primer impl (keyed DI) |
| `Application/CajaRegistradaAppService.cs` | Create | Upsert padrón + anti-SSRF (`Result<T>`) |
| `Application/ProveedorWebhookConfigAppService.cs` | Create | CRUD config + cifrado |
| `Infrastructure/Services/ICajaRegistradaCacheService.cs` (+ impl) | Create | Cache-aside hot path |
| `Endpoints/WebhookProveedorEndpoints.cs` | Create | Endpoint nuevo `POST /webhook/{proveedor}`: resolver proveedor por ruta + cuenta externa + tenant + validar firma entrante + encolar |
| `Endpoints/IngestEndpoints.cs` | — | **NO se modifica** (flujo 1-a-1 WooCommerce intacto, cero regresión) |
| `Endpoints/RegistroCajaEndpoints.cs` | Create | Auto-registro firmado + rate limit |
| `Endpoints/ProveedorConfigApiEndpoints.cs` | Create | Admin config por API |
| `Workers/WebhookDispatcherWorker.cs` | Modify | Enriquecer→rutear→forward dinámico; CB keyed por URL |
| `Program.cs` | Modify | Keyed DI providers, HttpClients, AppServices, cache, rate limiter |

## Contrato publicado (boundary — el gateway lo OWNS)

### A. Auto-registro de caja
- **Método/ruta**: `POST /registro-caja/{slug}` (slug del tenant).
- **Body JSON**:
  ```json
  { "identificador": "<string OPACO de caja>", "callbackUrl": "https://<tunel>.cfargotunnel.com/webhook" }
  ```
- **`identificador`**: string OPACA que el ERP genera libremente. El gateway la persiste y compara EXACTO; **no la sub-parsea**. Puede contener guiones; **única restricción: no puede contener `::`**.
- **Header de firma**: `X-WC-Webhook-Signature` (reusa esquema WooCommerce del tenant).
- **Algoritmo**: `base64(HMAC-SHA256(body, Tenant.WebhookSecret))` — convención base64 del proyecto. Se firma el **body crudo** (sin timestamp en v1).
- **Idempotencia**: upsert por `(TenantId, Identificador)`; re-registro actualiza `CallbackUrl` y `UltimaVez`. Respuesta `200 OK`.
- **Anti-SSRF**: `callbackUrl` debe ser `https://` y matchear la allowlist; si no → `400`.

### B. Payload reenviado a la caja
- **Forma**: RAW de MercadoPago verbatim (`{ "type": "...", "data": { "id": "..." } }` / `topic+id`). El enriquecido NO se propaga (minimiza PII).
- **Headers**: los seleccionados por `HeaderForwardingPolicy` (X-* del provider) + `X-Dotar-Gateway-ID: {slug}`.
- **Firma del gateway**: header `X-Dotar-Signature`, valor `base64(HMAC-SHA256(body, Tenant.WebhookSecret))` — mismo secret del tenant para ambas direcciones (Aclaración 1). La caja valida con su copia del secret.

### C. Formato del identificador
- **Estructura**: OPACA para el gateway. Es una string no vacía que **no contiene `::`**. Regex de validación: `^(?!.*::).+$`. El gateway NO interpreta su contenido (puede tener guiones); compara EXACTO contra `CajaRegistrada.Identificador`.
- **Extracción desde `external_reference`**: `external_reference = "{identificadorCaja}::{comprobante}"`. La routing key se obtiene con `external_reference.Split("::", 2)` → parte `[0]` = `identificadorCaja` (todo lo anterior al primer `::`). Si no hay `::` o la parte izquierda es vacía → `RoutingKeyResult.Invalid` → dead-letter. El `comprobante` es libre.

### D. Para el consumidor (ERP "DEAM Gestión")
El ERP debe: (1) auto-registrar cada caja al arrancar con su `identificador` OPACO (sección A — sin `::`); (2) generar órdenes MP con `external_reference = {identificadorCaja}::{comprobante}`, usando exactamente el mismo `identificador` que registró; (3) exponer un endpoint de callback que valide `X-Dotar-Signature` (sección B); (4) re-registrar al cambiar el túnel.

## Testing Strategy

| Layer | What | Approach |
|-------|------|----------|
| Unit | `MercadoPagoProvider.ExtraerRoutingKey` / `ResolverCuentaExterna` / `ValidarFirmaEntrante` | xUnit puro; casos: `::` presente/ausente/izquierda vacía, identificador con guiones, `user_id` presente/ausente, firma válida/inválida |
| Integration | `POST /webhook/{proveedor}` resolución tenant por cuenta externa | proveedor inexistente→404, `user_id` sin config→404, firma inválida→401, OK→202 + encola con `TenantId`+`ProveedorNombre` |
| Unit | `CajaRegistradaAppService` anti-SSRF + upsert | `Result<T>` asserts; allowlist mock |
| Unit | Cifrado `ProveedorWebhookConfigAppService` | `IDataProtector` real (ephemeral) round-trip |
| Integration | Worker enriquecer→rutear→forward / dead-letter | `IWebhookProvider` mock + `HttpClient` mock (mismo enfoque que tests de `ForwardingService`); DbContext SQLite in-memory |
| Integration | `POST /registro-caja` HMAC + idempotencia | `WebApplicationFactory`; re-registro actualiza URL |
| E2E | Ingest→cola→worker→callback (proveedor) y 1-a-1 (sin regresión) | `WebApplicationFactory` + Redis/HttpClient fakes |

`IWebhookProvider` mockeable (interfaz), `HttpClient` mockeable (`IHttpClientFactory` + handler fake), DbContext SQLite in-memory. TDD estricto: test primero por cada slice.

## Migration / Rollout

Migraciones EF Core aditivas (2 tablas, sin alterar `Tenant`): `AddCajaRegistrada`, `AddProveedorWebhookConfig`. Enum `DeadLetter` no requiere migración de esquema (columna `Status` es `string`, longitud holgada). Rollback: `dotnet ef migrations remove` / `Update-Database` al snapshot previo; flujo 1-a-1 intacto si la feature se desactiva. NO tocar `gateway.db` ni volúmenes.

## Plan de implementación incremental (slices)

1. **Modelo + migraciones**: entidades, `DbContext`, índices, `DeadLetter`, migraciones. (tests EF)
2. **Abstracción + MercadoPagoProvider**: `IWebhookProvider`, DTOs, impl MP, keyed DI, HttpClients. (tests unit provider)
3. **AppServices + cache + cifrado**: `CajaRegistradaAppService`, `ProveedorWebhookConfigAppService`, `ICajaRegistradaCacheService`. (tests unit + cifrado)
4. **Auto-registro + anti-SSRF + rate limit**: `RegistroCajaEndpoints`, allowlist. (tests integración HMAC/idempotencia)
5. **Endpoint de proveedor + worker**: nuevo `WebhookProveedorEndpoints` (`POST /webhook/{proveedor}`: resolver proveedor por ruta → `ResolverCuentaExterna` → lookup `(ProveedorNombre, CuentaExternaId)` → tenant → validar firma entrante → encolar con `TenantId`+`ProveedorNombre`); worker enriquecer→rutear→forward, dead-letter, CB keyed. `IngestEndpoints` NO se modifica. (tests integración + E2E del endpoint nuevo y regresión 1-a-1 sobre `/ingest/{slug}`)
6. **Admin API config + publicar contrato**. (tests integración)

## Open Questions

- [ ] Cap exacto del cache de pipelines CB (500 + LRU vs TTL 30 min) — decidir en apply según footprint real; ambas opciones documentadas.
