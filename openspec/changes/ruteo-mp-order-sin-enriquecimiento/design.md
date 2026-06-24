# Diseño técnico — Ruteo de notificaciones MP `type=order` sin enriquecimiento

**Cambio**: `ruteo-mp-order-sin-enriquecimiento`
**Enfoque**: A (sin enriquecimiento para `type=order`)
**Alcance**: cambio localizado, bajo riesgo, un solo PR (< 400 líneas).

Las notificaciones Point de MercadoPago llegan con `type=order` y `data.id` = ID de orden
(`ORD01KVX...`). El flujo actual llama `GET /v1/payments/{idOrden}` → MP responde 404 →
dead-letter → el pago nunca llega al ERP. Como la notificación ya trae `data.external_reference`
(el identificador de caja) y la firma `x-signature` ya fue validada en el endpoint de ingesta,
el enriquecimiento es innecesario para `order`: se rutea directo y se reenvía RAW.

---

## Quick path (qué cambia)

1. `IWebhookProvider` gana 2 métodos: `RutearSinEnriquecimiento(payload)` y
   `ExtraerRoutingKeyDesdeNotificacion(payloadNotificacion)`. El worker NO parsea JSON de MP.
2. `MercadoPagoProvider` implementa ambos: lee el `type` top-level y el `data.external_reference`
   anidado, reusando la misma regla `Split("__",2)[0]` que `ExtraerRoutingKey`.
3. `WebhookDispatcherWorker.ProcesarFlujoProveedorAsync` bifurca tras resolver el provider/config:
   si `RutearSinEnriquecimiento` es true → salta `ExtraerIdEvento` + `EnriquecerAsync` y usa
   `ExtraerRoutingKeyDesdeNotificacion`. El resto del flujo (caja, secret, forward RAW firmado,
   dead-letter) se reusa intacto.
4. `payment` queda exactamente igual (no-regresión).
5. Se actualiza la sección B del contrato-boundary (condicional por tipo).

---

## Decisión de acoplamiento (ADR-1)

**Decisión**: La detección del tipo y la lectura de la routing key anidada viven EN EL PROVIDER,
no en el worker. El worker pregunta al provider, vía un predicado y un extractor, ambos sobre el
payload entrante (`webhook.Payload`).

**Por qué**: el worker es agnóstico al proveedor (resuelve `IWebhookProvider` por keyed DI). Si el
worker parseara el campo `type` de MP o el `data.external_reference`, se acoplaría al formato de MP
y rompería la abstracción que ya existe (hoy el worker nunca parsea JSON específico de MP salvo
`ExtraerIdEvento`, que es genérico `data.id`/`id`). Encapsular en el provider mantiene la regla de
"el worker orquesta, el provider conoce el formato".

**Alternativa rechazada**: que el worker lea `type` directo del payload con `JsonDocument`.
Rechazada porque introduce conocimiento de MP en el worker y duplica la lógica de parseo de tipos
cuando se agreguen otros proveedores.

**Alternativa rechazada**: un solo método que devuelva tipo + routing key en un record.
Rechazada por ahora: dos métodos chicos son más testeables por separado y mantienen la firma de
`RoutingKeyResult` ya existente. Se puede consolidar más adelante si crece el número de tipos.

---

## Contratos de métodos nuevos

### `IWebhookProvider` (agregar)

```csharp
/// <summary>
/// Indica si la notificación entrante debe rutearse sin enriquecimiento
/// (la routing key ya viene en el payload firmado).
/// MP: true cuando el campo top-level "type" == "order".
/// Cualquier error de parseo → false (se cae al flujo enriquecido, comportamiento conservador).
/// </summary>
bool RutearSinEnriquecimiento(string payloadNotificacion);

/// <summary>
/// Extrae la routing key directamente desde la notificación entrante (sin enriquecer).
/// MP: lee data.external_reference (anidado) y aplica Split("__",2)[0] con las mismas reglas
/// que ExtraerRoutingKey (sin "__" / parte izquierda vacía / campo ausente / JSON inválido → Invalid).
/// </summary>
RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payloadNotificacion);
```

Notas de contrato:
- Reutilizan `RoutingKeyResult` (ya definido en `IWebhookProvider.cs`). No se crea tipo nuevo.
- `RutearSinEnriquecimiento` es conservador: ante JSON malformado o `type` ausente devuelve `false`,
  preservando el flujo `payment` (enriquecido) por defecto. Esto evita que un payload raro de `payment`
  se desvíe al camino directo.

### `MercadoPagoProvider` (implementar)

```csharp
public bool RutearSinEnriquecimiento(string payloadNotificacion)
{
    try
    {
        using var doc = JsonDocument.Parse(payloadNotificacion);
        if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            return string.Equals(t.GetString(), "order", StringComparison.OrdinalIgnoreCase);
        return false;
    }
    catch (JsonException) { return false; }
}

public RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payloadNotificacion)
{
    try
    {
        using var doc = JsonDocument.Parse(payloadNotificacion);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("external_reference", out var prop) ||
            prop.ValueKind != JsonValueKind.String)
            return RoutingKeyResult.Invalid;

        return ParsearRoutingKey(prop.GetString());   // helper privado compartido
    }
    catch (JsonException ex)
    {
        _logger.LogWarning(ex, "Notificación MP order no es JSON válido al extraer routing key");
        return RoutingKeyResult.Invalid;
    }
}
```

**Refactor mínimo (DRY)**: extraer la lógica de `Split("__",2)[0]` a un helper privado
`ParsearRoutingKey(string? externalRef)` y que tanto `ExtraerRoutingKey` (lee de la raíz) como
`ExtraerRoutingKeyDesdeNotificacion` (lee de `data`) lo invoquen. Esto evita duplicar las reglas de
validación (sin `__`, parte izquierda vacía, vacío → Invalid). El helper:

```csharp
private static RoutingKeyResult ParsearRoutingKey(string? externalRef)
{
    if (string.IsNullOrEmpty(externalRef)) return RoutingKeyResult.Invalid;
    var partes = externalRef.Split("__", 2);
    if (partes.Length < 2 || string.IsNullOrEmpty(partes[0])) return RoutingKeyResult.Invalid;
    return RoutingKeyResult.Valido(partes[0]);
}
```

> El refactor de `ExtraerRoutingKey` para que use el helper es opcional pero recomendado; no cambia
> su comportamiento observable (los tests existentes deben seguir verdes). Si se prefiere riesgo cero,
> dejar `ExtraerRoutingKey` como está y solo usar el helper en el método nuevo. **Recomendación: usar
> el helper en ambos** (los tests de `ExtraerRoutingKey` cubren la no-regresión).

---

## Flujo bifurcado en `ProcesarFlujoProveedorAsync`

La bifurcación se inserta **después** de resolver provider + config (pasos 1–2 actuales, líneas
~193–285) y **antes** de `ExtraerIdEvento` (línea ~289). El tramo común posterior a obtener la
routing key (buscar caja → secret → forward RAW firmado → dead-letter) NO se toca.

```
ProcesarFlujoProveedorAsync(webhook):
  1. resolver IWebhookProvider (keyed DI)                 ── sin cambios
  2. obtener + descifrar ProveedorWebhookConfig            ── sin cambios
  ┌─ BIFURCACIÓN NUEVA ────────────────────────────────────────────────┐
  │  if provider.RutearSinEnriquecimiento(webhook.Payload):              │
  │      log Worker "tipo=order ruteo directo"                           │
  │      routingKeyResult = provider.ExtraerRoutingKeyDesdeNotificacion( │
  │                              webhook.Payload)                        │
  │  else:                                                               │
  │      // flujo enriquecido actual (payment), intacto:                 │
  │      idEvento = ExtraerIdEvento(webhook.Payload)                     │
  │      if vacío → dead-letter "id_evento_no_extraible"; return         │
  │      enrich = await provider.EnriquecerAsync(idEvento, cfg, ct)      │
  │      if !enrich.Exitoso → dead-letter "error_enriquecimiento"; ret   │
  │      routingKeyResult = provider.ExtraerRoutingKey(enrich.Payload)   │
  └────────────────────────────────────────────────────────────────────┘
  3. if !routingKeyResult.EsValido → dead-letter "external_reference_invalida"; return  ── COMÚN
  4. caja = cajaCache.GetByIdentificador(...) ; if null → dead-letter "caja_no_encontrada"  ── COMÚN
  5. webhookSecret = configEntidad.Tenant.WebhookSecret ; if vacío → dead-letter "secret_tenant_ausente"  ── COMÚN
  6. forward RAW (webhook.Payload) a caja.CallbackUrl con X-Caja-Signature  ── COMÚN
  7. SaveDeliveryLog (Success | Scheduled), ForwardClientName="CajaCallback"  ── COMÚN
```

**Implementación concreta**: declarar `RoutingKeyResult routingKeyResult;` antes del `if`, asignarla
en cada rama, y dejar todo el bloque a partir de "Extraer routing key" (línea ~320) trabajando sobre
esa variable. Las ramas comparten exactamente el mismo código desde el chequeo `!EsValido`.

**Motivos de dead-letter para `order`** (todos ya existen, se reutilizan):
- `external_reference_invalida`: sin `data.external_reference`, sin `__`, o parte izquierda vacía.
- `caja_no_encontrada`: identificador no está en el padrón del tenant.
- `secret_tenant_ausente`: tenant sin `WebhookSecret`.

`order` NO produce `id_evento_no_extraible` ni `error_enriquecimiento` (no aplica el enriquecimiento).

---

## Observabilidad

Loguear el tipo detectado en `SystemLog` con categoría `Worker` (consistente con el log existente
de "caja no encontrada", que ya usa `SystemLogCategory.Worker`):

- Rama `order` (al entrar a la bifurcación):
  ```csharp
  _systemLog.Info(SystemLogCategory.Worker,
      $"Notificación '{proveedorNombre}' tipo=order: ruteo directo sin enriquecimiento.",
      eventId: eventId,
      details: $"proveedor={proveedorNombre}; tenantId={webhook.TenantId}; modo=sin_enriquecimiento");
  ```
- Rama `payment`: no se agrega log nuevo (el flujo ya tiene su trazabilidad). Opcionalmente un
  `_logger.LogDebug` para el modo enriquecido; no se requiere `SystemLog`.

El `details` incluye `modo=sin_enriquecimiento` para poder filtrar en `/logs`.

---

## Impacto en el contrato-boundary

Archivo: `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md`, **sección B**.

Cambiar la lista numerada de "Cuando MercadoPago notifica al Gateway... el Gateway:" para reflejar el
condicional por tipo. Reemplazar los pasos 2–3 por una descripción bifurcada:

> 1. Valida la firma entrante de MercadoPago (`x-signature`).
> 2. **Según el `type` de la notificación**:
>    - `type=order` (pagos Point / Orders API): la routing key se lee directamente de
>      `data.external_reference` de la notificación **firmada**. No se enriquece.
>    - otros tipos (ej. `payment`): enriquece con `GET /v1/payments/{id}` y lee `external_reference`
>      de la raíz del recurso enriquecido.
> 3. Extrae el `identificador` de caja desde `external_reference` con `Split("__",2)[0]`.
> 4. Reenvía el payload **RAW** (verbatim) al `callbackUrl` registrado, con `X-Caja-Signature`.

Notas adicionales en sección B / C:
- Aclarar que para `order`, `external_reference` viaja anidado en `data.external_reference` dentro de
  la notificación; para `payment` viaja en la raíz del payload enriquecido. La regla de extracción
  (`Split("__",2)[0]`) es idéntica.
- Subir versión del documento de `1.0` a `1.1` y mencionar el cambio en un breve changelog si el
  formato lo admite (si no, solo bump de versión).

No cambian: firma `X-Caja-Signature`, formato del identificador, allowlist, ni el comportamiento del
endpoint de auto-registro (sección A).

---

## Estrategia de tests (TDD — escribir ANTES del código)

Test runner: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`.
Strict TDD: cada test rojo antes de su implementación.

### Provider — `tests/.../Providers/MercadoPagoProviderTests.cs`

| # | Test | Método | Aserción |
|---|------|--------|----------|
| P1 | `RutearSinEnriquecimiento_TypeOrder_RetornaTrue` | `RutearSinEnriquecimiento` | `{"type":"order","data":{...}}` → true |
| P2 | `RutearSinEnriquecimiento_TypePayment_RetornaFalse` | id. | `{"type":"payment",...}` → false |
| P3 | `RutearSinEnriquecimiento_SinType_RetornaFalse` | id. | payload sin `type` → false |
| P4 | `RutearSinEnriquecimiento_JsonInvalido_RetornaFalse` | id. | `"no-json"` → false |
| P5 | `RutearSinEnriquecimiento_TypeOrderMayusculas_RetornaTrue` | id. | `"Order"` → true (case-insensitive) |
| P6 | `ExtraerRoutingKeyDesdeNotificacion_ConSeparador_RetornaParteIzquierda` | `ExtraerRoutingKeyDesdeNotificacion` | `data.external_reference="CAJA-01__0001"` → `CAJA-01` |
| P7 | `ExtraerRoutingKeyDesdeNotificacion_GuionBajoSimple` | id. | `"003-CAJA_2__260624"` → `003-CAJA_2` |
| P8 | `ExtraerRoutingKeyDesdeNotificacion_SinSeparador_Invalido` | id. | `"CAJA-01"` → Invalid |
| P9 | `ExtraerRoutingKeyDesdeNotificacion_ParteIzquierdaVacia_Invalido` | id. | `"__c"` → Invalid |
| P10 | `ExtraerRoutingKeyDesdeNotificacion_SinExternalReferenceEnData_Invalido` | id. | `data` sin `external_reference` → Invalid |
| P11 | `ExtraerRoutingKeyDesdeNotificacion_SinData_Invalido` | id. | payload sin `data` → Invalid |
| P12 | `ExtraerRoutingKeyDesdeNotificacion_JsonInvalido_Invalido` | id. | `"no-json"` → Invalid |
| P13 (no-regresión) | tests existentes de `ExtraerRoutingKey` siguen verdes | `ExtraerRoutingKey` | sin cambios (cubre el refactor del helper) |

### Worker — `tests/.../Workers/WebhookDispatcherWorkerTests.cs`

El fake `FakeProviderForWorker` (líneas 828–853) DEBE extenderse para implementar los 2 métodos
nuevos. Propuesta:

```csharp
public bool RutearSinEnriquecimientoValor { get; set; } = false;
public RoutingKeyResult RoutingKeyDesdeNotificacionResult { get; set; } = RoutingKeyResult.Valido("CAJA-01");
public bool RutearSinEnriquecimientoLlamado { get; private set; }

public bool RutearSinEnriquecimiento(string payload)
{
    RutearSinEnriquecimientoLlamado = true;
    return RutearSinEnriquecimientoValor;
}
public RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payload)
    => RoutingKeyDesdeNotificacionResult;
```

| # | Test | Aserción clave |
|---|------|----------------|
| W1 | `Worker_TipoOrder_RuteaDirecto_SinEnriquecer` | `RutearSinEnriquecimientoValor=true`, routing key válida, caja registrada → 1 forward RAW a callbackUrl con X-Caja-Signature; `EnriquecimientoLlamado == false` |
| W2 | `Worker_TipoOrder_ExternalReferenceInvalida_DeadLetter` | `RoutingKeyDesdeNotificacionResult = Invalid` → dead-letter `external_reference_invalida`, sin forward, `EnriquecimientoLlamado == false` |
| W3 | `Worker_TipoOrder_CajaNoEncontrada_DeadLetter` | routing key válida pero caja ausente → dead-letter `caja_no_encontrada` |
| W4 | `Worker_TipoOrder_NoLlamaExtraerIdEvento_NiEnriquecer` | `EnriquecimientoLlamado == false` (cubre que NO se hace `GET /v1/payments`) |
| W5 (no-regresión) | `Worker_TipoPayment_SigueEnriqueciendo` | `RutearSinEnriquecimientoValor=false` → `EnriquecimientoLlamado == true`, usa `ExtraerRoutingKey` (no el método de notificación) |
| W6 (no-regresión) | tests `payment` existentes (5.6–5.11) siguen verdes | sin cambios funcionales |

Fakes reutilizables sin cambios: `CapturingForwardingService`, `FakeCajaCache`,
`FakeKeyedServiceProvider`, `FailingThenCapturingForwardingService`, `FakeQueueForWorker`.
Único fake a extender: `FakeProviderForWorker` (agregar los 2 métodos + flags).

> Nota sobre payloads de test existentes: varios usan `{"topic":"payment",...}`. El campo real de MP
> para el tipo es `type`, no `topic`. Como `FakeProviderForWorker` controla `RutearSinEnriquecimiento`
> por flag, los tests del worker no dependen del JSON real. Los tests del PROVIDER (P1–P5) sí deben
> usar el campo correcto `type`. No se modifican los payloads de los tests de payment existentes.

---

## Riesgos y mitigaciones

| Riesgo | Severidad | Mitigación |
|--------|-----------|------------|
| Regresión en `payment` (camino enriquecido) | Alta | Default conservador (`RutearSinEnriquecimiento=false` ante duda) + tests W5/W6 + tests payment existentes intactos. |
| Refactor de `ExtraerRoutingKey` rompe su comportamiento | Media | El helper `ParsearRoutingKey` preserva la lógica exacta; los tests existentes de `ExtraerRoutingKey` (P13) validan no-regresión. Si hay dudas, no refactorizar `ExtraerRoutingKey` y usar el helper solo en el método nuevo. |
| `type` real de MP difiere de `"order"` (mayúsculas/variante) | Media | Comparación `OrdinalIgnoreCase`; test P5. Si MP usa otro literal, ajustar el único string en `MercadoPagoProvider`. |
| `external_reference` ausente en notificaciones `order` reales | Media | Dead-letter `external_reference_invalida` (degradación segura, no 404). Aceptado: peor caso = mismo dead-letter que hoy, sin loop de enriquecimiento. |
| Contrato-boundary desincronizado con el código | Baja | Actualización de sección B en el mismo PR; bump de versión a 1.1. |
| Dead-letters productivos `order` ya perdidos | Baja | Fuera de alcance (non-goal aceptado en la propuesta). |

---

## Resumen de archivos a tocar

| Archivo | Cambio |
|---------|--------|
| `src/Dotar.Gateway/Providers/IWebhookProvider.cs` | +2 firmas de método |
| `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs` | +2 implementaciones, +1 helper privado `ParsearRoutingKey`, refactor opcional de `ExtraerRoutingKey` |
| `src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs` | bifurcación en `ProcesarFlujoProveedorAsync` (~8–12 líneas) + 1 log Worker |
| `tests/.../Providers/MercadoPagoProviderTests.cs` | +12 tests (P1–P12) |
| `tests/.../Workers/WebhookDispatcherWorkerTests.cs` | extender `FakeProviderForWorker` + 5 tests (W1–W5) |
| `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md` | sección B condicional por tipo + bump versión |

Estimación: < 400 líneas → un solo PR.
