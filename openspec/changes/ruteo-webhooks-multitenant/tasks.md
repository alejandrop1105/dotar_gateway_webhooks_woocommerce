# Tasks: Ruteo multi-tenant de webhooks con enriquecimiento por proveedor

## Review Workload Forecast

| Campo | Valor |
|-------|-------|
| Líneas estimadas | 1 100 – 1 400 (código + tests) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Corte sugerido | PR1: modelo + provider · PR2: appservices + auto-registro · PR3: endpoint proveedor + worker · PR4: admin API + contrato |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending (requiere decisión del usuario antes de apply) |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Unidades de trabajo sugeridas

| Unidad | Objetivo | PR sugerido | Base / dependencia |
|--------|----------|-------------|--------------------|
| WU-1 | Modelo EF + migraciones + `DeadLetter` + `ProveedorNombre` en `QueuedWebhook` | PR 1 | `master` / rama tracker |
| WU-2 | `IWebhookProvider` + `MercadoPagoProvider` + keyed DI + HttpClients | PR 1 (mismo, ≤400 lín juntos) o PR 2 si supera | Depende de WU-1 |
| WU-3 | AppServices + cache + cifrado | PR 2 | Depende de WU-1 |
| WU-4 | Auto-registro `POST /registro-caja/{slug}` + anti-SSRF + rate limit | PR 3 | Depende de WU-3 |
| WU-5 | Endpoint `POST /webhook/{proveedor}` + modificación worker + dead-letter | PR 4 | Depende de WU-2 + WU-3 |
| WU-6 | Admin API config (`ProveedorConfigApiEndpoints`) + publicar contrato | PR 4 (mismo) o PR 5 | Depende de WU-3 |

---

## Slice 1 — Modelo + migraciones (WU-1)

### 1.1 RED: test EF — entidades y constraints
- [x] 1.1 Escribir test `CajaRegistrada_Constraints_Test`: verificar índice único `(TenantId, Identificador)` con SQLite in-memory. Confirmar que un upsert duplicado falla la constraint.
- [x] 1.2 Escribir test `ProveedorWebhookConfig_Constraints_Test`: verificar índice único `(TenantId, ProveedorNombre)` e índice único `(ProveedorNombre, CuentaExternaId)` con SQLite in-memory.

### 1.2 GREEN: entidades y DbContext
- [x] 1.3 Crear `src/Dotar.Gateway/Domain/Entities/CajaRegistrada.cs` con campos: `Id`, `TenantId`, `Tenant`, `Identificador` (`MaxLength(100)`, opaco), `CallbackUrl` (`MaxLength(2000)`), `CreatedAt`, `UpdatedAt`, `UltimaVez`.
- [x] 1.4 Crear `src/Dotar.Gateway/Domain/Entities/ProveedorWebhookConfig.cs` con campos: `Id`, `TenantId`, `Tenant`, `ProveedorNombre`, `CuentaExternaId` (`MaxLength(100)`), `CredencialesCifradas` (`TEXT`), `BaseUrl`, `IsActive`, `CreatedAt`, `UpdatedAt`. Sin `SecretProveedor` como columna plana (va dentro de `CredencialesCifradas`).
- [x] 1.5 Modificar `src/Dotar.Gateway/Infrastructure/Data/GatewayDbContext.cs`: agregar `DbSet<CajaRegistrada>` y `DbSet<ProveedorWebhookConfig>`; configurar en `OnModelCreating` los 3 índices únicos, `MaxLength`, `HasColumnType("TEXT")`, y `OnDelete(Cascade)` en ambas FK.
- [x] 1.6 Modificar `src/Dotar.Gateway/Domain/Entities/DeliveryLog.cs`: agregar valor `DeadLetter` al enum `DeliveryStatus` (compatible con `HasConversion<string>()` existente).
- [x] 1.7 Modificar `src/Dotar.Gateway/Domain/Models/QueuedWebhook.cs`: agregar `public string? ProveedorNombre { get; set; }`.
- [x] 1.8 Ejecutar migración: `dotnet ef migrations add AddRuteoMultitenant --project src/Dotar.Gateway/Dotar.Gateway.csproj` — migración `20260622115301_AddRuteoMultitenant` generada correctamente.
- [x] 1.9 Verificar regresión: dotnet test → 190/190 tests verdes (184 anteriores + 6 nuevos EF).

---

## Slice 2 — Abstracción `IWebhookProvider` + `MercadoPagoProvider` (WU-2)

### Verificación temprana obligatoria (antes de continuar)
- [x] 2.0 **VERIFICAR CONTRA PAYLOAD REAL**: confirmado (ver prompt + hechos verificados): `user_id` viene en el body de la notificación entrante de MP. Documentado en `MercadoPagoProvider.cs`.

### 2.1 RED: tests unitarios del provider
- [x] 2.1 Escribir tests `MercadoPagoProvider_ResolverCuentaExterna_Test`: `user_id` presente retorna id; `user_id` ausente retorna `null`; body malformado retorna `null`.
- [x] 2.2 Escribir tests `MercadoPagoProvider_ExtraerRoutingKey_Test`: `external_reference = "CAJA-01::0001"` → `"CAJA-01"`; identificador opaco con guiones `"CAJA-ESPECIAL-01::0002"` → `"CAJA-ESPECIAL-01"`; sin `::` → `RoutingKeyResult.Invalid`; `external_reference` ausente → `Invalid`; parte izquierda vacía `"::comprobante"` → `Invalid`.
- [x] 2.3 Escribir tests `MercadoPagoProvider_ValidarFirmaEntrante_Test`: firma MP `x-signature` válida → `true`; firma inválida → `false`; header ausente → `false`.
- [x] 2.4 Escribir test `MercadoPagoProvider_EnriquecerAsync_Test`: `HttpMessageHandler` fake que retorna pago JSON → `EnrichmentResult.Exitoso = true`; handler retorna 5xx → `Exitoso = false`.

### 2.2 GREEN: abstracción e implementación
- [x] 2.5 Crear `src/Dotar.Gateway/Providers/IWebhookProvider.cs`: interfaz con `Nombre`, `ResolverCuentaExterna`, `ValidarFirmaEntrante`, `EnriquecerAsync`, `ExtraerRoutingKey`; DTOs `RoutingKeyResult` y `EnrichmentResult` en el mismo archivo.
- [x] 2.6 Crear `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs`: impl completa con firma timing-safe (CryptographicOperations.FixedTimeEquals), manifest MP correcto, GET con Bearer, Split("::").
- [x] 2.7 Modificar `src/Dotar.Gateway/Program.cs`: `AddKeyedSingleton<IWebhookProvider, MercadoPagoProvider>("mercadopago")`; `"ProviderEnrichment"` (timeout 10 s) y `"CajaCallback"` (`AllowAutoRedirect = false`).
- [x] 2.8 Verificar tests del slice 2 verdes + regresión completa → 207/207 tests verdes.

---

## Slice 3 — AppServices + cache + cifrado (WU-3)

### 3.1 RED: tests unitarios AppServices y cache
- [x] 3.1 Escribir tests `CajaRegistradaAppService_AntiSSRF_Test`: `callbackUrl` con `http://` → `Result.Validation`; dominio fuera de allowlist → `Result.Validation`; URL válida en allowlist → `Result.Success`.
- [x] 3.2 Escribir tests `CajaRegistradaAppService_Upsert_Test`: registro nuevo persiste; re-registro con mismo `(TenantId, Identificador)` actualiza `CallbackUrl` y `UltimaVez` sin duplicar.
- [x] 3.3 Escribir tests `ProveedorWebhookConfigAppService_Cifrado_Test`: cifrado round-trip con `IDataProtector` ephemeral; credenciales no aparecen en texto plano en la entidad persistida.
- [x] 3.4 Escribir tests `ICajaRegistradaCacheService_Test`: miss llama `GatewayDbContext`; hit retorna desde cache; `Invalidate` limpia la entrada y fuerza próximo miss.

### 3.2 GREEN: AppServices y cache
- [x] 3.5 Crear `src/Dotar.Gateway/Application/CajaRegistradaAppService.cs`: `RegistrarAsync` con validación anti-SSRF (allowlist desde `IConfiguration`) + upsert por `(TenantId, Identificador)` + actualización `UltimaVez` + `Invalidate` del cache; retorna `Result<CajaDto>`.
- [x] 3.6 Crear `src/Dotar.Gateway/Application/ProveedorWebhookConfigAppService.cs`: `UpsertAsync` cifra `CredencialesCifradas` vía `IDataProtector("ProveedorWebhookConfig.Credenciales.v1")`; lookups por `(ProveedorNombre, CuentaExternaId)` y por `(TenantId, ProveedorNombre)` con descifrado en memoria; no expone valores en claro en DTOs de respuesta.
- [x] 3.7 Crear `src/Dotar.Gateway/Infrastructure/Services/ICajaRegistradaCacheService.cs` e impl `CajaRegistradaCacheService.cs`: Singleton, `IMemoryCache` cache-aside, abre scope vía `IServiceScopeFactory` para `GatewayDbContext` en miss; excluye cajas cuya `UltimaVez` supere el TTL configurado.
- [x] 3.8 Modificar `Program.cs`: registrar `CajaRegistradaAppService` y `ProveedorWebhookConfigAppService` (Scoped); registrar `ICajaRegistradaCacheService → CajaRegistradaCacheService` (Singleton); leer `Seguridad:CallbackDominiosPermitidos` y `Seguridad:CajaTtlMinutos` desde `appsettings.json`.
- [x] 3.9 Agregar keys de configuración a `appsettings.json`: `"Seguridad": { "CallbackDominiosPermitidos": ["*.cfargotunnel.com","*.dotarsoluciones.com"], "CajaTtlMinutos": 30 }`.
- [x] 3.10 Verificar tests del slice 3 verdes + regresión completa.

---

## Slice 4 — Auto-registro `POST /registro-caja/{slug}` (WU-4)

### 4.1 RED: tests de integración del endpoint
- [x] 4.1 Escribir test integración `RegistroCaja_HMAC_Valido_Retorna200`: con `WebApplicationFactory`; body + HMAC válidos → 200 + caja en DB.
- [x] 4.2 Escribir test `RegistroCaja_HMAC_Invalido_Retorna401`.
- [x] 4.3 Escribir test `RegistroCaja_SinHMAC_Retorna401`.
- [x] 4.4 Escribir test `RegistroCaja_Idempotencia_ActualizaCallbackUrl`: mismo `identificador`, segunda URL → 200, URL actualizada, sin registro duplicado.
- [x] 4.5 Escribir test `RegistroCaja_IdentificadorConDobleColon_Retorna400`.
- [x] 4.6 Escribir test `RegistroCaja_CallbackUrlHttp_Retorna400`.
- [x] 4.7 Escribir test `RegistroCaja_CallbackUrlFueraDeAllowlist_Retorna400`.
- [x] 4.8 Escribir test `RegistroCaja_TenantNoEncontrado_Retorna404`.
- [x] 4.9 Escribir test `RegistroCaja_RateLimit_Retorna429`: superar límite configurado → 429.

### 4.2 GREEN: endpoint de auto-registro
- [x] 4.10 Crear `src/Dotar.Gateway/Endpoints/RegistroCajaEndpoints.cs`: `POST /registro-caja/{slug}`; leer body crudo; verificar `X-Caja-Signature` (HMAC-SHA256 hex lowercase con `WebhookSecret` del tenant); validar campos `identificador` (no vacío, no contiene `::`) y `callbackUrl`; delegar a `CajaRegistradaAppService.RegistrarAsync`; mapear `Result<T>` a códigos HTTP (200/400/401/404/429).
- [x] 4.11 Modificar `Program.cs`: registrar rate limiter (fixed window 10 req/min por IP) con `AddRateLimiter`; aplicar a `RegistroCajaEndpoints`.
- [x] 4.12 Verificar tests del slice 4 verdes + regresión completa.

---

## Slice 5 — Endpoint de proveedor + worker (WU-5)

### 5.1 RED: tests de integración del endpoint proveedor
- [ ] 5.1 Escribir test `WebhookProveedor_ProveedorInexistente_Retorna404`: ruta `/webhook/stripe` sin keyed DI → 404.
- [ ] 5.2 Escribir test `WebhookProveedor_CuentaExternaDesconocida_Retorna404`: `user_id` sin `ProveedorWebhookConfig` → 404 + log `Ingest`.
- [ ] 5.3 Escribir test `WebhookProveedor_FirmaInvalida_Retorna401`: config resuelta pero `x-signature` inválida → 401 + log `Ingest`.
- [ ] 5.4 Escribir test `WebhookProveedor_FirmaValida_Retorna202`: payload MP válido → 202 + `QueuedWebhook` con `TenantId` + `ProveedorNombre = "mercadopago"` en Redis.
- [ ] 5.5 Escribir test `IngestEndpoint_NoRegresion`: `POST /ingest/{slug}` con payload WooCommerce → comportamiento idéntico a hoy (tests existentes).

### 5.2 RED: tests del worker — flujo proveedor
- [ ] 5.6 Escribir test worker `Worker_EnriquecerYRutear_ExitoReenviaRAW`: `IWebhookProvider` mock + `ICajaRegistradaCacheService` mock con caja viva + `HttpClient` fake → forward a `CallbackUrl` con header `X-Caja-Signature` (HMAC-SHA256 hex lowercase); payload reenviado es el RAW original.
- [ ] 5.7 Escribir test worker `Worker_CajaNoEncontrada_DeadLetter`: caja no en padrón → `DeliveryStatus.DeadLetter` + log `Worker`.
- [ ] 5.8 Escribir test worker `Worker_ExternalReferenceInvalida_DeadLetter`: `RoutingKeyResult.Invalid` → dead-letter + log `Forward`.
- [ ] 5.9 Escribir test worker `Worker_ErrorEnriquecimiento_DeadLetter`: `EnrichmentResult.IsSuccess = false` → dead-letter + log `Forward`.
- [ ] 5.10 Escribir test worker `Worker_SinProveedorNombre_Flujo1a1`: `ProveedorNombre = null` → `ForwardAsync(TargetUrl)` sin llamar al provider (no regresión).
- [ ] 5.11 Escribir test worker `Worker_DeadLetterNoBloqueaProcesamiento`: mensaje `A` dead-letter, mensaje `B` procesado correctamente en la misma ejecución del worker.

### 5.3 GREEN: endpoint + modificación worker
- [ ] 5.12 Crear `src/Dotar.Gateway/Endpoints/WebhookProveedorEndpoints.cs`: `POST /webhook/{proveedor}`; leer body crudo; resolver `IWebhookProvider` por keyed DI (`proveedor` de ruta) → 404 si no existe; llamar `ResolverCuentaExterna` → 404 si null; lookup `ProveedorWebhookConfigAppService` por `(ProveedorNombre, CuentaExternaId)` → 404 si sin match; `ValidarFirmaEntrante` → 401 + log `Auth` si falla; `EnqueueAsync(QueuedWebhook { TenantId, ProveedorNombre, RawPayload })` → 202.
- [ ] 5.13 Modificar `src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs`: en `ProcessNewWebhookAsync`, bifurcar por `webhook.ProveedorNombre`: si null → flujo existente (`ForwardAsync(TargetUrl)`); si no null → resolver `IWebhookProvider` → `EnriquecerAsync` → `ExtraerRoutingKey` → `ICajaRegistradaCacheService.GetByIdentificadorAsync` → si caja encontrada `ForwardAsync(caja.CallbackUrl, rawPayload, "CajaCallback")` → si no encontrada dead-letter. Cambiar `ConcurrentDictionary` de CB de keyed por `TenantId` a keyed por `callbackUrl` (string) con cap 500 + TTL deslizante 30 min.
- [ ] 5.14 Verificar que `IngestEndpoints.cs` NO fue modificado (diff vacío para ese archivo).
- [ ] 5.15 Verificar tests del slice 5 verdes + regresión completa: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`.

---

## Slice 6 — Admin API config + contrato (WU-6)

### 6.1 RED: tests de integración admin API
- [ ] 6.1 Escribir test `ProveedorConfig_SinAutenticacion_Retorna401`.
- [ ] 6.2 Escribir test `ProveedorConfig_UpsertNuevo_Retorna201`: crea config con `CuentaExternaId` + credenciales cifradas.
- [ ] 6.3 Escribir test `ProveedorConfig_UpsertExistente_Retorna200_SinDuplicado`: actualiza `CuentaExternaId` y credenciales; un único registro en DB.
- [ ] 6.4 Escribir test `ProveedorConfig_Listar_NoExponeCredenciales`: respuesta no contiene `access_token` ni `SecretProveedor` en texto plano.

### 6.2 GREEN: admin API + publicación de contrato
- [ ] 6.5 Crear `src/Dotar.Gateway/Endpoints/ProveedorConfigApiEndpoints.cs`: `POST /api/proveedores/config` (upsert) y `GET /api/proveedores/config` (listar sin valores sensibles); protegido por `ApiKeyEndpointFilter` existente.
- [ ] 6.6 Agregar sección de configuración `MercadoPago:BaseUrl` a `appsettings.json` (`"https://api.mercadopago.com"`).
- [ ] 6.7 Publicar contrato del boundary en `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md` (secciones A–D del design).
- [ ] 6.8 Verificar tests del slice 6 verdes + regresión final completa: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` → ≥ 184 tests verdes.
