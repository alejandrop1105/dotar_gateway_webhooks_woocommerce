# Proposal: Ruteo de pedidos WooCommerce por sucursal (WooCommerceMultiSucursal)

## Intent

Un pedido hecho en WooCommerce debe BAJAR automáticamente a la máquina física de la sucursal que lo vendió. Hoy WooCommerce en el Gateway funciona solo 1-a-1 (`Tenant.TargetUrl` fijo): todos los pedidos van a un único destino, sin separar por sucursal. Falta el ruteo dinámico por sucursal — análogo al que ya existe para MercadoPago (que rutea por caja) — para que cada pedido aterrice en la máquina correcta. Un pedido no ruteado es una venta que no llega a la sucursal: impacto directo de negocio.

**Por qué "MultiSucursal" y no "WooCommerce" a secas**: este ruteo NO funciona con un WooCommerce estándar. Requiere que un plugin de WordPress inyecte el código de sucursal dentro del payload del pedido (en `meta_data`). El provider NO depende de un plugin específico: lee la sucursal de una ubicación **configurable** del payload, así que sirve para cualquier plugin que cumpla ese contrato. MultiLocal es un *ejemplo* de plugin que lo cumple, no un requisito del diseño. Por eso el nombre describe la capacidad (`WooCommerceMultiSucursal`), no un producto concreto.

**Insight de diseño (destacado)**: este cambio es MÁS SIMPLE que MercadoPago. Reusa el ruteo dinámico por destino (caja/sucursal) pero NO la capa de resolución multi-cuenta. Tenant resuelto por URL (`slug`), sucursal resuelta por `meta_data`. Menos superficie de código nuevo y de error.

## Scope

### In Scope
- `WooCommerceMultiSucursalProvider : IWebhookProvider` (nuevo, keyed DI `"woocommerce-multisucursal"`): valida firma entrante `X-WC-Webhook-Signature` (base64 HMAC-SHA256 del payload con el `WebhookSecret` del tenant), siempre rutea sin enriquecimiento, y extrae la sucursal del `meta_data` configurado.
- Configuración por tenant: **key del `meta_data`** que contiene la sucursal (configurable, no hardcodeada) + **separador opcional** (igual que MP usa `__`).
- Registro del provider en `Program.cs` (keyed DI) y wiring de la config necesaria.

### Out of Scope (NON-GOALS explícitos)
- Enriquecimiento contra la API de WooCommerce (fallback cuando falta el `meta_data`) → mejora futura explícita.
- Fan-out a múltiples sucursales por pedido → v1 es 1 pedido → 1 sucursal.
- Alerta push (email/Slack/webhook) para pedidos no ruteables → v1 usa SystemLog de severidad alta en `/logs`.
- Resolución multi-cuenta (`CuentaExterna`) → una URL = un tenant; no aplica.
- Acoplamiento a un plugin concreto (MultiLocal u otro) → la ubicación de la sucursal en el payload es configurable.
- Tooling/UI de administración de la key del `meta_data` → fuera del primer slice si excede el wiring mínimo.

## Capabilities

### New Capabilities
- `woocommerce-multisucursal-routing`: implementación `IWebhookProvider` para WooCommerce con plugin multi-sucursal — firma entrante, ruteo sin enriquecimiento y extracción de sucursal desde `meta_data` configurable.

### Modified Capabilities
None. Reusa sin modificar la infra de flujo por proveedor (`CajaRegistrada`, `RegistroCajaEndpoints`, `WebhookDispatcherWorker`, `CajaRegistradaCacheService`).

## Approach

- **Reutilización máxima**: el ruteo dinámico ya existe (change `ruteo-webhooks-multitenant`). Lo único nuevo es un provider y su config; NO se duplica padrón, worker, firma de salida ni dead-letter.
  - Padrón: `CajaRegistrada` donde `Identificador` = sucursal; el ERP registra la URL de cada máquina vía `POST /registro-caja/{slug}` (heartbeat/TTL existente).
  - Worker: `WebhookDispatcherWorker` resuelve la caja, firma `X-Caja-Signature`, aplica circuit breaker, reintentos y dead-letter — sin cambios.
- **Flujo del provider** (vs. `MercadoPagoProvider`):
  - `ResolverCuentaExterna` → no aplica (tenant viene del `slug` de la URL); resolución por URL, no por payload.
  - `ValidarFirmaEntrante` → `X-WC-Webhook-Signature` base64 HMAC-SHA256, timing-safe.
  - `RutearSinEnriquecimiento` → siempre `true` para los eventos de pedido.
  - `ExtraerRoutingKeyDesdeNotificacion` → lee la **key configurable** del `meta_data`, aplica separador opcional, devuelve la sucursal.
  - `EnriquecerAsync` / `ExtraerRoutingKey` → no se usan en v1 (no hay enriquecimiento).
- **Eventos ruteados**: `order.created`, `order.updated`, `order.deleted`. Todos bajan a la máquina de la sucursal; el filtrado fino lo hace el ERP destino.
- **Pedido no ruteable** (falta `meta_data` / sucursal no registrada / registro vencido / value con formato inesperado) → dead-letter (mecanismo existente) + SystemLog de severidad alta visible en `/logs`. Mejorable a futuro.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Providers/WooCommerceMultiSucursalProvider.cs` | New | Provider keyed DI `"woocommerce-multisucursal"`; firma, ruteo sin enriquecimiento, extracción de sucursal. |
| `Program.cs` | Modified | Registro keyed del nuevo provider + wiring de config. |
| `Domain/Entities/Tenant.cs` (o config equivalente) | Modified | Key del `meta_data` + separador por tenant (a confirmar dónde persiste). |
| `Providers/IWebhookProvider.cs` | Reused | Sin cambios — contrato existente. |
| `Workers/WebhookDispatcherWorker.cs`, `Endpoints/RegistroCajaEndpoints.cs`, `CajaRegistradaCacheService` | Reused | Sin cambios. |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Formato exacto del value del `meta_data` desconocido | High | BLOQUEANTE para go-live, NO para diseño. Confirmar contra payload real; key+separador parametrizados por tenant. |
| Pedido no ruteado = venta perdida en la sucursal | High | Dead-letter + SystemLog severidad alta en `/logs`; el ERP recupera. Alerta robusta = follow-up. |
| Ruido de `order.updated` (muchas actualizaciones por pedido) | Med | Todos bajan; filtrado fino en el ERP destino. Revisar volumen en monitoreo. |
| Mecanismo de alerta v1 (solo log) insuficiente | Med | Marcado explícitamente como mejorable; retomar manejo de no ruteables. |
| Plugin del WordPress no inyecta la sucursal en el payload | High | Precondición del feature: sin plugin que inyecte el código de sucursal, no hay ruteo. Documentado como dependencia externa. |

## Rollback Plan

Revertir el commit/PR. El provider es aditivo (keyed DI nuevo + config); no altera el flujo 1-a-1 ni el ruteo MP existentes. Sin migraciones destructivas (la config del `meta_data` es aditiva). Tenants productivos y `gateway.db`/volúmenes intactos.

## Dependencies

- Un plugin de WordPress que inyecte el código de sucursal en el `meta_data` del pedido (p.ej. MultiLocal). Es precondición del feature.
- Payload real con ese `meta_data` para confirmar key y formato del value (bloqueante para go-live).
- Infra del change `ruteo-webhooks-multitenant` (padrón + worker + registro de caja) ya desplegada.
- ERP destino registrando la URL de cada máquina por sucursal vía `POST /registro-caja/{slug}`.

## Success Criteria

- [ ] Un pedido WooCommerce con firma válida y sucursal en `meta_data` se rutea a la máquina registrada de esa sucursal.
- [ ] `order.created`, `order.updated` y `order.deleted` bajan a la sucursal correcta.
- [ ] Firma `X-WC-Webhook-Signature` inválida → rechazo (sin reenvío).
- [ ] Pedido sin `meta_data` / sucursal no registrada / registro vencido → dead-letter + SystemLog severidad alta en `/logs`.
- [ ] La key del `meta_data` y el separador son configurables por tenant (no hardcodeados).
- [ ] El provider no asume un plugin concreto: cambiar la key del `meta_data` basta para soportar otro plugin que inyecte la sucursal.
- [ ] Flujo 1-a-1, ruteo MP y tenants productivos sin regresión.

## Proposal question round (asunciones a validar)

Estas asunciones ya fueron validadas con el usuario y se formalizan acá; quedan abiertas solo para confirmación final:
1. Formato del value del `meta_data` (¿solo sucursal? ¿`{sucursal}{sep}{otro}`?) — a confirmar contra payload real antes de go-live.
2. Dónde persiste la config key+separador (¿campos nuevos en `Tenant` o tabla de config de proveedor?) — a resolver en design.
3. Mecanismo de alerta para no ruteables: v1 = SystemLog severidad alta; el usuario quiere retomar una mejora futura.
