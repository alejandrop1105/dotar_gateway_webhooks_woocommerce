# Delta for tenant-application-service

## MODIFIED Requirements

### Requirement: Despacho de webhook — resolución de destino por padrón

El sistema DEBE resolver el destino del reenvío a través del padrón de cajas (`CajaRegistrada`) cuando el tenant tiene un proveedor configurado (`ProveedorWebhookConfig`). El `WebhookDispatcherWorker` DEBE usar el resultado del ruteo por proveedor (ver `webhook-provider-routing`) para obtener la `callbackUrl` dinámica. El despacho 1-a-1 a `Tenant.TargetUrl` DEBE mantenerse únicamente para tenants SIN proveedor configurado; para tenants CON proveedor, si la caja no se encuentra en el padrón, el worker DEBE ir a dead-letter — NUNCA fallback a `Tenant.TargetUrl`.

(Previously: el worker siempre usaba `Tenant.TargetUrl` como destino único de reenvío, independientemente del origen del webhook.)

#### Scenario: Despacho a caja mediante padrón (tenant con proveedor)

- GIVEN un tenant con `ProveedorWebhookConfig` para `"mercadopago"` y una `CajaRegistrada` con `Identificador = "SUC1-C01"`
- WHEN el worker procesa un `QueuedWebhook` con `ProveedorNombre = "mercadopago"` y el flujo de ruteo resuelve `"SUC1-C01"`
- THEN el reenvío se hace a la `CallbackUrl` de `"SUC1-C01"`, NO a `Tenant.TargetUrl`

#### Scenario: Despacho 1-a-1 para tenant sin proveedor (no regresión)

- GIVEN un tenant SIN `ProveedorWebhookConfig` configurada (flujo WooCommerce clásico)
- WHEN el worker procesa un `QueuedWebhook` para ese tenant
- THEN el reenvío se hace a `Tenant.TargetUrl` exactamente igual que antes de este cambio

#### Scenario: Caja no encontrada no hace fallback a TargetUrl

- GIVEN un tenant con proveedor configurado
- WHEN el worker completa el ruteo y la caja no se encuentra en el padrón
- THEN el webhook va a dead-letter y NO se reenvía a `Tenant.TargetUrl`

---

## ADDED Requirements

### Requirement: ProveedorNombre en QueuedWebhook

El sistema DEBE incluir el campo `ProveedorNombre` (string, nullable) en `QueuedWebhook`. El ingest DEBE asignar `ProveedorNombre` cuando el tenant tiene exactamente un proveedor configurado. El worker DEBE usar `ProveedorNombre` para resolver el `IWebhookProvider` sin re-parsear el payload.

#### Scenario: ProveedorNombre asignado en el ingest

- GIVEN un tenant con un único proveedor `"mercadopago"` configurado
- WHEN el ingest encola un webhook entrante para ese tenant
- THEN el `QueuedWebhook` en Redis contiene `ProveedorNombre = "mercadopago"`

#### Scenario: ProveedorNombre nulo para tenant sin proveedor

- GIVEN un tenant sin `ProveedorWebhookConfig` (flujo clásico)
- WHEN el ingest encola un webhook para ese tenant
- THEN el `QueuedWebhook` contiene `ProveedorNombre = null`

---

### Requirement: No regresión — tests existentes siguen verdes

Los ~184 tests existentes DEBEN continuar pasando tras la incorporación del despacho por padrón. Ningún cambio en `QueuedWebhook`, `WebhookDispatcherWorker` ni `TenantAppService` DEBE romper el contrato observable de los endpoints existentes ni los tests de integración HTTP actuales.

#### Scenario: Suite completa sin regresión

- GIVEN los 184 tests existentes en `tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`
- WHEN se ejecuta `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`
- THEN todos los tests pasan con el mismo resultado que antes de este cambio
