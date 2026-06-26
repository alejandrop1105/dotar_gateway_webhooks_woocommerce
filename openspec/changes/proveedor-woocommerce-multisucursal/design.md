# Design — proveedor-woocommerce-multisucursal

Diseño técnico para rutear pedidos de WooCommerce a la máquina física de cada sucursal,
leyendo el código de sucursal desde una ubicación CONFIGURABLE del `meta_data` del pedido.
Reusa el ruteo dinámico por caja (CajaRegistrada + WebhookDispatcherWorker + X-Caja-Signature)
SIN modificarlo, y NO usa enriquecimiento por API ni resolución multi-cuenta.

## Quick path (cómo encaja end-to-end)

1. WordPress (plugin que inyecta el código de sucursal en `meta_data`) hace `POST /ingest/{slug}` con firma WooCommerce.
2. `IngestEndpoints` valida la firma HMAC base64 (igual que hoy) y, si el tenant está en **modo proveedor**, encola con `ProveedorNombre = "woocommerce-multisucursal"`.
3. `WebhookDispatcherWorker` bifurca por `ProveedorNombre` → rama proveedor (ya existente).
4. La rama proveedor resuelve el provider keyed, salta enriquecimiento (`RutearSinEnriquecimiento → true`), extrae la sucursal del `meta_data` (lógica pura), busca la caja y reenvía RAW con `X-Caja-Signature`.
5. Si no es ruteable (sin meta / sucursal no registrada / vencida) → dead-letter + SystemLog de severidad alta visible en `/logs`.

## Decisiones de arquitectura (ADR)

### ADR-1 — Resolución de tenant: extender `/ingest/{slug}` (opción B)

**Decisión.** Reusar el endpoint existente `POST /ingest/{slug}` agregando un **modo "ruteo por proveedor"** al `Tenant`. Cuando el tenant está en ese modo, `IngestEndpoints`:

- valida la firma WooCommerce con `HmacSignatureValidator` (ya lo hace hoy, sin cambios), y
- encola el `QueuedWebhook` con `ProveedorNombre` seteado al nombre del provider del tenant,

lo que dispara la rama proveedor del worker (ruteo por caja) sin tocar el endpoint `/webhook/{proveedor}` ni el flujo de MercadoPago.

**Por qué.**

| Criterio | Por qué B gana |
|----------|----------------|
| Firma WooCommerce | El secreto WooCommerce vive en `Tenant.WebhookSecret` y la firma se valida en `IngestEndpoints`. `/ingest/{slug}` ya tiene ese pipeline; `/webhook/{proveedor}` valida vía `provider.ValidarFirmaEntrante(config)`, que lee de `CredencialesCifradas`, no del secret del tenant. |
| Tenant por slug | El requisito es 1 WordPress = 1 tenant resuelto por slug de URL. `/ingest/{slug}` ya resuelve por slug. `/webhook/{proveedor}` resuelve por `CuentaExterna` del payload (multi-cuenta), que NO queremos. |
| Superficie nueva | B agrega un flag + nombre de provider al `Tenant` y unas líneas en el handler. No agrega endpoint, no agrega ruta, no toca el router. |
| No romper flujo 1-a-1 | El modo es opt-in por tenant; los tenants existentes mantienen `ProveedorNombre = null` y caen en `ProcesarFlujo1a1Async` sin cambio observable. |

**Alternativas descartadas.**

- **(A) Endpoint nuevo `/webhook/{proveedor}/{slug}`.** Duplica el pipeline de firma WooCommerce que ya existe en `/ingest`, agrega ruta y handler, y obliga a reconfigurar la URL del webhook en cada WordPress productivo. Más superficie, cero beneficio.
- **(C) Reusar `/webhook/{proveedor}` adaptando la resolución de tenant.** Obligaría a `ResolverCuentaExterna` a devolver algo derivado del slug (que no está en el payload de WooCommerce, está en la URL), y la firma se validaría con `CredencialesCifradas` en vez del `WebhookSecret`. Romper el contrato de `/webhook/{proveedor}` pone en riesgo a MercadoPago (productivo).

**Contrato concreto del handler `/ingest/{slug}` (cambio mínimo).** Tras validar firma y antes de encolar:

```csharp
// Nuevo: si el tenant rutea por proveedor, marcar el QueuedWebhook para la rama proveedor del worker.
string? proveedorNombre = tenant.RuteoProveedorActivo ? tenant.ProveedorRuteoNombre : null;

await queue.EnqueueAsync(new QueuedWebhook
{
    TenantId = tenant.Id,
    TenantSlug = tenant.Slug,
    TargetUrl = tenant.TargetUrl, // ignorado por la rama proveedor; el worker resuelve por caja
    SourceUrl = sourceUrl,
    Payload = payload,
    ReceivedAt = DateTime.UtcNow,
    ForwardedHeaders = forwardedHeaders,
    EventId = eventId,
    ProveedorNombre = proveedorNombre   // null para tenants 1-a-1 (comportamiento intacto)
});
```

No se cambia la firma de `HandleIngest`, no se cambia la validación HMAC, no se cambia el `RedisQueueService`.

### ADR-2 — Persistencia de la config: campos aditivos en `Tenant` (no `ProveedorWebhookConfig`)

**Decisión.** Agregar columnas **nullable** a la entidad `Tenant`. WooCommerce v1 NO tiene credenciales, así que no encaja en `ProveedorWebhookConfig` (cuya razón de ser es `CuentaExternaId` + `CredencialesCifradas`).

| Campo nuevo en `Tenant` | Tipo | Default | Significado |
|-------------------------|------|---------|-------------|
| `RuteoProveedorActivo` | `bool` | `false` | Activa el modo "ruteo por proveedor" en `/ingest/{slug}`. |
| `ProveedorRuteoNombre` | `string?` (nullable) | `null` | Clave keyed DI del provider (ej. `"woocommerce-multisucursal"`). Solo se usa si `RuteoProveedorActivo`. |
| `SucursalMetaKey` | `string?` (nullable) | `null` | Key dentro del `meta_data` del pedido que contiene el código de sucursal (ej. `_sucursal_codigo`). |
| `SucursalMetaSeparador` | `string?` (nullable) | `null` | Separador opcional. Si está seteado, se aplica `Split(separador)[0]` sobre el value (paridad con el `"__"` de MP). Si es `null`/vacío, se usa el value completo trim. |

**Por qué `Tenant` y no `ProveedorWebhookConfig`.**

- `ProveedorWebhookConfig` requiere `CuentaExternaId` (lookup multi-cuenta que NO usamos) y `CredencialesCifradas` (no hay credenciales). Reusarla obligaría a inventar valores dummy y a pasar por el AppService de descifrado.
- La config de ruteo es **1-a-1 con el tenant** (un WordPress = un tenant = una key de meta). Vive naturalmente en `Tenant`.
- Mantiene la rama proveedor del worker desacoplada de `ProveedorWebhookConfig` para WooCommerce (ver ADR-3).

**Migración EF concreta.**

- Nombre: `AgregarRuteoProveedorWooCommerceMultiSucursal`
- Comando: `dotnet ef migrations add AgregarRuteoProveedorWooCommerceMultiSucursal --project src/Dotar.Gateway/Dotar.Gateway.csproj`
- Contenido: 4 columnas aditivas, todas nullable o con default `false`. Es **estrictamente aditiva**: no renombra, no borra, no cambia tipos. SQLite la aplica con `ALTER TABLE ADD COLUMN`, sin reescribir la tabla.

```csharp
migrationBuilder.AddColumn<bool>(
    name: "RuteoProveedorActivo", table: "Tenants",
    type: "INTEGER", nullable: false, defaultValue: false);

migrationBuilder.AddColumn<string>(
    name: "ProveedorRuteoNombre", table: "Tenants",
    type: "TEXT", maxLength: 100, nullable: true);

migrationBuilder.AddColumn<string>(
    name: "SucursalMetaKey", table: "Tenants",
    type: "TEXT", maxLength: 200, nullable: true);

migrationBuilder.AddColumn<string>(
    name: "SucursalMetaSeparador", table: "Tenants",
    type: "TEXT", maxLength: 20, nullable: true);
```

Los tenants existentes quedan con `RuteoProveedorActivo = false` → flujo 1-a-1 intacto. No se rotan secrets, no se tocan tenants productivos.

**Alternativa descartada — tabla nueva.** Una tabla `RuteoProveedorConfig(TenantId, ...)` agrega un join y un AppService nuevo para una relación que es estrictamente 1-a-1 con el tenant. Sobrediseño para v1.

### ADR-3 — La rama proveedor del worker exige `ProveedorWebhookConfig`: branch sin credenciales

**Problema verificado.** En `WebhookDispatcherWorker.ProcesarFlujoProveedorAsync` (paso 2), si no encuentra un `ProveedorWebhookConfig` activo para `(TenantId, ProveedorNombre)`, hace dead-letter con motivo `config_proveedor_no_encontrada`. WooCommerce multisucursal NO tendrá ese registro.

**Decisión.** Introducir en `IWebhookProvider` una **capacidad declarativa** que diga si el provider necesita config con credenciales, y bifurcar el paso 2 del worker en función de ella:

```csharp
// Nuevo miembro en IWebhookProvider
/// <summary>
/// Indica si el provider requiere un ProveedorWebhookConfig con credenciales (enriquecimiento por API).
/// MercadoPago: true. WooCommerce multisucursal: false (rutea solo desde el payload entrante).
/// </summary>
bool RequiereConfigProveedor { get; }
```

- `MercadoPagoProvider.RequiereConfigProveedor => true` (comportamiento actual intacto).
- `WooCommerceMultiSucursalProvider.RequiereConfigProveedor => false`.

En el worker, el paso 2 se envuelve:

```csharp
ProveedorWebhookConfig? configParaProvider = null;
ProveedorWebhookConfig? configEntidad = null;

if (provider.RequiereConfigProveedor)
{
    // ... bloque actual de búsqueda + descifrado + dead-letter si falta (SIN CAMBIOS) ...
}
// else: WooCommerce no carga config; configParaProvider queda null y nunca se usa
//       porque RutearSinEnriquecimiento => true salta EnriquecerAsync.
```

**Cómo pasa WooCommerce cada etapa de la rama proveedor:**

| Etapa del worker | MercadoPago | WooCommerce multisucursal |
|------------------|-------------|---------------------------|
| Resolver provider keyed | sí | sí |
| Cargar+descifrar `ProveedorWebhookConfig` | sí (dead-letter si falta) | **se salta** (`RequiereConfigProveedor=false`) |
| `RutearSinEnriquecimiento(payload)` | true solo si `type=order` | **siempre true** |
| `EnriquecerAsync` | solo rama payment | **nunca** |
| `ExtraerRoutingKeyDesdeNotificacion(payload)` | `data.external_reference` | **`meta_data[key]` (lógica pura nueva)** |
| Buscar caja (`ICajaRegistradaCacheService`) | sí | sí (idéntico) |
| Firmar y reenviar (`X-Caja-Signature`) | sí | sí (idéntico) |

**Detalle del WebhookSecret para firmar el callback.** Hoy el worker obtiene el secret vía `configEntidad.Tenant?.WebhookSecret` (cargado con `Include` en el paso 2). Como WooCommerce salta ese bloque, el worker debe obtener el `Tenant` por otra vía cuando `RequiereConfigProveedor=false`: cargar el `Tenant` por `webhook.TenantId` en un scope (`AsNoTracking`) para leer `WebhookSecret`. Esto es un branch aditivo dentro del mismo paso; el camino de MercadoPago no cambia.

**Por qué una capacidad declarativa y no un `if (proveedorNombre == "woocommerce...")`.** Mantiene el worker agnóstico del proveedor concreto (Open/Closed): cualquier provider futuro sin enriquecimiento se suma declarando `RequiereConfigProveedor=false`, sin tocar el worker otra vez.

**Alternativa descartada — config mínima dummy.** Crear un `ProveedorWebhookConfig` con `CredencialesCifradas="{}"` solo para pasar el chequeo es deuda: agrega una fila sin sentido, obliga a la UI a gestionarla, y acopla WooCommerce al lookup de credenciales que no necesita.

### ADR-4 — Alerta v1 de no ruteable: SystemLog `Worker`/`Error` (severidad alta)

**Decisión.** Cuando un pedido no es ruteable, además del dead-letter ya existente, emitir un `SystemLog` con:

- **Categoría:** `SystemLogCategory.Worker` (es una falla de ruteo interno, no de Forward/red).
- **Severidad:** `Error` (`systemLog.Error(...)`), para que destaque sobre los `Warn` en `/logs`.
- **Dónde:** en el tramo común de la rama proveedor del worker, en los tres caminos de no ruteable:
  - sucursal ausente / meta inválido → routing key inválida,
  - caja `NoEncontrada`,
  - caja `Vencida`.

**Por qué.** Las categorías existentes (`Ingest, Forward, Retry, ManualRetry, Auth, Worker, Tunnel, Api, System`) ya cubren el caso: `Worker` es exactamente "el worker no pudo rutear". No se inventa categoría nueva (aditividad mínima). Subir a `Error` (hoy varios de estos caminos usan `Warn`) es lo que da la "severidad alta" visible y filtrable en `/logs`. El push/alerta externa queda como mejora futura (fuera de v1).

**Detalle de detalle.** El `details` del log debe incluir `proveedor`, `tenantId`, `identificador` (si se extrajo) y `motivo` (`sucursal_ausente` | `caja_no_encontrada` | `caja_vencida`) para diagnóstico en el dashboard.

## Diseño del `WooCommerceMultiSucursalProvider`

Clase nueva: `src/Dotar.Gateway/Providers/WooCommerceMultiSucursalProvider.cs`, implementa `IWebhookProvider`.
Es **más simple** que MercadoPago: sin HttpClient, sin credenciales, sin enriquecimiento.

| Miembro de `IWebhookProvider` | Qué hace en WooCommerce multisucursal |
|-------------------------------|----------------------------------------|
| `Nombre` | `"woocommerce-multisucursal"` (clave keyed DI). |
| `RequiereConfigProveedor` | `false` (ADR-3). |
| `RutearSinEnriquecimiento(payload)` | **siempre `true`** — WooCommerce nunca enriquece. |
| `ExtraerRoutingKeyDesdeNotificacion(payload)` | **núcleo**: parsea el body del pedido, encuentra el item de `meta_data` cuya `key` coincide, toma su `value`, aplica separador opcional y devuelve `RoutingKeyResult`. |
| `ResolverCuentaExterna(headers, body)` | `null` — no aplica (no se usa en `/ingest/{slug}`; solo lo invoca `/webhook/{proveedor}`, que no usamos). |
| `ValidarFirmaEntrante(headers, body, config)` | `false` — no aplica; la firma WooCommerce la valida `IngestEndpoints` con `HmacSignatureValidator`. Documentar que este método no es el camino de firma de este provider. |
| `EnriquecerAsync(...)` | `EnrichmentResult.Fallo("no soportado")` — nunca invocado porque `RutearSinEnriquecimiento=true`. Defensa por contrato. |
| `ExtraerRoutingKey(payloadEnriquecido)` | `RoutingKeyResult.Invalid` — nunca invocado (solo lo usa la rama enriquecimiento). |

### Separación lógica pura vs I/O (testabilidad — clave para TDD)

El provider NO hace I/O en su núcleo. La extracción de sucursal es una **función pura** `byte[]/string → RoutingKeyResult`, testeable sin HTTP, sin Redis, sin DB.

Para que la key y el separador (que viven en `Tenant`, ADR-2) lleguen a la función pura, se extrae la lógica a una clase estática reutilizable, parametrizada:

```csharp
// src/Dotar.Gateway/Providers/SucursalMetaDataExtractor.cs  (lógica PURA, estática)
public static class SucursalMetaDataExtractor
{
    /// <summary>
    /// Extrae el código de sucursal del array meta_data de un pedido WooCommerce.
    /// Pura: sin I/O. Reglas → RoutingKeyResult.Invalid:
    ///   - JSON inválido,
    ///   - falta meta_data o no es array,
    ///   - ningún item con key == metaKey,
    ///   - value vacío tras aplicar separador.
    /// Si separador no es null/vacío: aplica value.Split(separador)[0].Trim().
    /// </summary>
    public static RoutingKeyResult Extraer(string payload, string metaKey, string? separador);
}
```

- El **provider** (`ExtraerRoutingKeyDesdeNotificacion`) NO puede recibir `metaKey`/`separador` porque la firma de `IWebhookProvider` no los pasa. Dos opciones de wiring resueltas abajo.

**Wiring de key/separador hacia el provider.** El contrato `IWebhookProvider.ExtraerRoutingKeyDesdeNotificacion(payload)` no incluye la config del tenant. Para no romper la interfaz:

- Decisión: **inyectar la sucursal en el `payload` que recibe el provider NO es viable** (el provider recibe el RAW). En su lugar, el provider lee `metaKey`/`separador` desde un **valor por convención embebido en el propio `meta_data`** NO — eso acopla. 

  Decisión final: el worker, cuando `RequiereConfigProveedor=false`, ya carga el `Tenant` (para el `WebhookSecret`). Se aprovecha ese `Tenant` para pasar `SucursalMetaKey`/`SucursalMetaSeparador` al provider mediante una **sobrecarga específica del provider WooCommerce** que el worker invoca por contrato extendido. Para mantener `IWebhookProvider` estable, se agrega un método opcional al contrato:

```csharp
// Añadir a IWebhookProvider (default interface method para no romper MercadoPago)
RoutingKeyResult ExtraerRoutingKeyConConfig(string payload, Tenant tenant)
    => ExtraerRoutingKeyDesdeNotificacion(payload); // default: ignora tenant
```

  - `WooCommerceMultiSucursalProvider` **override** `ExtraerRoutingKeyConConfig(payload, tenant)` → llama `SucursalMetaDataExtractor.Extraer(payload, tenant.SucursalMetaKey!, tenant.SucursalMetaSeparador)`.
  - `MercadoPagoProvider` usa el default (ignora tenant) → comportamiento intacto.
  - El worker, en el tramo de ruteo sin enriquecimiento, llama `ExtraerRoutingKeyConConfig(payload, tenant)` en vez de `ExtraerRoutingKeyDesdeNotificacion(payload)`. Como MercadoPago usa el default, su flujo no cambia.

  > Nota de implementación: si el equipo prefiere no tocar `IWebhookProvider` con un default method, la alternativa equivalente es un type-check `provider is WooCommerceMultiSucursalProvider` en el worker. Se prefiere el método de contrato por Open/Closed; el tasks-phase puede elegir, pero el default-interface-method es la recomendación.

## Componentes y flujo de datos

```
WordPress (plugin meta_data)
   │  POST /ingest/{slug}  (firma WooCommerce base64)
   ▼
IngestEndpoints.HandleIngest
   │  tenant por slug → validar HMAC (HmacSignatureValidator) → [ADR-1]
   │  if tenant.RuteoProveedorActivo: ProveedorNombre = tenant.ProveedorRuteoNombre
   ▼
RedisQueueService (QueuedWebhook { ProveedorNombre })
   ▼
WebhookDispatcherWorker.ProcesarFlujoProveedorAsync
   │  resolver provider keyed
   │  if provider.RequiereConfigProveedor → cargar config (MP) else cargar Tenant para WebhookSecret  [ADR-3]
   │  RutearSinEnriquecimiento → true (WC)  → ExtraerRoutingKeyConConfig(payload, tenant)  [ADR-3/provider]
   │      └─ SucursalMetaDataExtractor.Extraer(payload, metaKey, separador)  ← lógica PURA
   │  routingKey inválida? → dead-letter + SystemLog Worker/Error  [ADR-4]
   ▼
ICajaRegistradaCacheService.ResolverAsync(tenantId, sucursal)
   │  NoEncontrada / Vencida → dead-letter + SystemLog Worker/Error  [ADR-4]
   ▼
ForwardWithCircuitBreakerAsync(caja.CallbackUrl, payload, { X-Caja-Signature })  ← REUSO total
```

## Puntos de extensión y qué se reusa SIN tocar

| Componente | Acción |
|------------|--------|
| `RedisQueueService`, `QueuedWebhook` | reuso; `ProveedorNombre` ya existe. |
| `CajaRegistrada`, `/registro-caja`, `ICajaRegistradaCacheService` | reuso total. |
| `ForwardWithCircuitBreakerAsync` (callback), `X-Caja-Signature`, dead-letter, retries | reuso total. |
| `HmacSignatureValidator` (WooCommerce base64) | reuso total. |
| `MercadoPagoProvider`, `/webhook/{proveedor}` | NO se tocan. |
| `IWebhookProvider` | extender con `RequiereConfigProveedor` + `ExtraerRoutingKeyConConfig` (default method). |
| `IngestEndpoints.HandleIngest` | +3 líneas (set `ProveedorNombre` según tenant). |
| `WebhookDispatcherWorker` paso 2 | envolver carga de config en `if (RequiereConfigProveedor)`; branch para cargar Tenant. |
| `Tenant` | +4 columnas nullable (migración aditiva). |
| `Program.cs` | +1 registro keyed (`woocommerce-multisucursal`). |

## Wiring en `Program.cs`

```csharp
builder.Services.AddKeyedSingleton<IWebhookProvider, WooCommerceMultiSucursalProvider>(
    "woocommerce-multisucursal",
    (sp, _) => new WooCommerceMultiSucursalProvider(
        sp.GetRequiredService<ILogger<WooCommerceMultiSucursalProvider>>()));
```

Sin HttpClient (no enriquece). Se agrega junto al registro existente de `mercadopago`.

## Estrategia de testing (TDD — `dotnet test tests/Dotar.Gateway.Tests/...`)

### Unit — lógica pura (`SucursalMetaDataExtractor`)

Casos (sin HTTP/DB/Redis):

- [ ] meta_data con key presente, sin separador → value trim como routing key.
- [ ] key presente con separador `"__"` y value `"003__extra"` → `"003"`.
- [ ] key presente con value vacío → `Invalid`.
- [ ] key ausente en el array → `Invalid`.
- [ ] `meta_data` ausente o no-array → `Invalid`.
- [ ] JSON inválido → `Invalid`.
- [ ] value con guiones/underscore simple sin separador configurado → se conserva entero.
- [ ] separador presente pero value sin separador → value completo (paridad con MP `Split(.,2)`).

### Unit — provider

- [ ] `RutearSinEnriquecimiento(cualquier payload)` → `true`.
- [ ] `RequiereConfigProveedor` → `false`.
- [ ] `ExtraerRoutingKeyConConfig` delega correctamente en el extractor con la key/separador del tenant.
- [ ] `EnriquecerAsync` → `Fallo` (contrato defensivo, nunca usado).

### Integración — worker (reusa `ProcesarWebhookParaTestAsync` + fakes existentes)

- [ ] `ProveedorNombre="woocommerce-multisucursal"` + caja registrada → forward a `CallbackUrl` con `X-Caja-Signature`.
- [ ] sucursal no registrada → dead-letter `caja_no_encontrada` + SystemLog `Worker/Error`.
- [ ] sucursal vencida → dead-letter `caja_vencida` + SystemLog `Worker/Error`.
- [ ] meta ausente → dead-letter `sucursal_ausente` + SystemLog `Worker/Error`.
- [ ] **no regresión**: `ProveedorNombre=null` sigue en flujo 1-a-1; MercadoPago (`RequiereConfigProveedor=true`) sigue cargando config.

### Integración — endpoint

- [ ] `/ingest/{slug}` con firma WooCommerce válida y `RuteoProveedorActivo=true` → encola con `ProveedorNombre` seteado.
- [ ] `/ingest/{slug}` con `RuteoProveedorActivo=false` → encola con `ProveedorNombre=null` (sin cambio).

## Checklist de diseño

- [ ] Tenant resuelto por slug, no por cuenta externa (ADR-1).
- [ ] Migración EF aditiva, nullable, sin tocar tenants productivos (ADR-2).
- [ ] Worker no exige `ProveedorWebhookConfig` cuando `RequiereConfigProveedor=false` (ADR-3).
- [ ] No ruteable → SystemLog `Worker/Error` visible en `/logs` (ADR-4).
- [ ] Lógica pura de extracción aislada y unit-testeada (TDD).
- [ ] MercadoPago y flujo 1-a-1 sin cambios observables.

## Riesgos residuales

1. **Formato del `value` del meta_data** (bloqueante de go-live, no de diseño): el separador opcional cubre las variantes conocidas, pero el value real debe confirmarse contra un payload de producción del plugin. Si el código viene anidado (no plano), el extractor necesita un ajuste menor.
2. **Default interface method en `IWebhookProvider`**: requiere C# moderno (lo soporta .NET 9). Si el equipo rechaza default methods, el fallback es el type-check en el worker — decisión delegable a tasks.
3. **WebhookSecret para firmar el callback**: el branch sin config debe cargar el `Tenant`; si el tenant no tiene `WebhookSecret`, el comportamiento es el mismo que MP (dead-letter `secret_tenant_ausente`).
4. **Doble responsabilidad de `/ingest/{slug}`**: el endpoint pasa a tener dos modos. El riesgo de confusión se mitiga manteniendo el branch mínimo (set de `ProveedorNombre`) y cubriéndolo con tests de endpoint.
5. **Alerta solo-log en v1**: si nadie mira `/logs`, los no ruteables pasan inadvertidos hasta el push (mejora futura). Aceptado para v1.
