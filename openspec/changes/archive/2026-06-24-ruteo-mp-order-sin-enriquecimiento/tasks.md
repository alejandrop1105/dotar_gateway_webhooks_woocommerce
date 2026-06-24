# Tasks — ruteo-mp-order-sin-enriquecimiento

**Change**: ruteo-mp-order-sin-enriquecimiento  
**Fecha**: 2026-06-24  
**TDD activo**: sí — test runner: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj`  
**Delivery**: single PR (~280 líneas, bajo presupuesto 400)

---

## Review Workload Forecast

| Métrica | Estimación |
|---------|-----------|
| Archivos tocados | 6 |
| Líneas añadidas (aprox.) | ~230 |
| Líneas modificadas (aprox.) | ~50 |
| Total changed lines (aprox.) | ~280 |
| Budget 400 líneas | OK — bajo presupuesto |
| Chained PRs recommended | No — single PR |
| Decision needed before apply | No |

---

## WU-1 — Contratos nuevos en IWebhookProvider + helper DRY en MercadoPagoProvider

**Requisitos cubiertos**: REQ-1 (detección de tipo), REQ-2 (extracción routing key desde notificación)  
**Secuencial**: base para WU-2 y WU-3

### T-1.1 — Tests P1–P5: RutearSinEnriquecimiento (RED)

- [ ] Archivo: `tests/Dotar.Gateway.Tests/Providers/MercadoPagoProviderTests.cs`
- Escenarios:
  - P1: `{"type":"order",...}` → `true` (REQ-1 esc. 1)
  - P2: `{"type":"payment",...}` → `false` (REQ-1 esc. 2)
  - P3: payload sin campo `type` → `false` (REQ-1 esc. 3, conservador)
  - P4: JSON inválido → `false` (sin excepción)
  - P5: `{"type":"ORDER",...}` → `true` (OrdinalIgnoreCase)
- Verificación: `dotnet test ... --filter "FullyQualifiedName~RutearSinEnriquecimiento"` → todos FAIL (método no existe aún)

### T-1.2 — Declarar e implementar RutearSinEnriquecimiento (GREEN)

- [ ] `src/Dotar.Gateway/Providers/IWebhookProvider.cs`: añadir `bool RutearSinEnriquecimiento(string payloadNotificacion);`
- [ ] `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs`: implementar — parsear JSON, leer `type` top-level, OrdinalIgnoreCase == "order"; JSON inválido / ausente → false.
- Verificación: P1–P5 pasan.

### T-1.3 — Tests P6–P12: ExtraerRoutingKeyDesdeNotificacion (RED)

- [ ] Archivo: `tests/Dotar.Gateway.Tests/Providers/MercadoPagoProviderTests.cs`
- Escenarios:
  - P6: `data.external_reference = "003-CAJA_2__260624140146"` → `Valido("003-CAJA_2")` (REQ-2 esc. 1)
  - P7: `data.external_reference = "CAJA_1__ORD-001"` → `Valido("CAJA_1")` (REQ-2 esc. 2)
  - P8: sin `__` en external_reference → `Invalid` (REQ-2 esc. 4)
  - P9: parte izquierda vacía `"__comprobante"` → `Invalid` (REQ-2 esc. 5)
  - P10: sin campo `data.external_reference` → `Invalid` (REQ-2 esc. 3)
  - P11: sin campo `data` en payload → `Invalid` (REQ-2 esc. 3 variante)
  - P12: JSON inválido → `Invalid` (sin excepción)
- Verificación: `dotnet test ... --filter "FullyQualifiedName~ExtraerRoutingKeyDesdeNotificacion"` → todos FAIL

### T-1.4 — Refactor DRY ParsearRoutingKey + implementar ExtraerRoutingKeyDesdeNotificacion (GREEN)

- [ ] `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs`: extraer helper privado `ParsearRoutingKey(string? externalRef)` con lógica Split("__",2)[0] actual.
- [ ] Refactorizar `ExtraerRoutingKey` para delegar en `ParsearRoutingKey` (lee raíz).
- [ ] `src/Dotar.Gateway/Providers/IWebhookProvider.cs`: declarar `RoutingKeyResult ExtraerRoutingKeyDesdeNotificacion(string payloadNotificacion);`
- [ ] `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs`: implementar — parsear JSON, leer `data.external_reference` (anidado), llamar `ParsearRoutingKey`.
- Verificación: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` → P6–P12 pasan + tests existentes de ExtraerRoutingKey siguen verdes.

### T-1.5 — Test P13: no-regresión ExtraerRoutingKey post-refactor (RED → GREEN)

- [ ] Verificar (o añadir si no existe) test que confirme que `ExtraerRoutingKey` (flujo payment, lee raíz) sigue funcionando tras el refactor DRY (REQ-4).
- Verificación: `dotnet test ... --filter "FullyQualifiedName~ExtraerRoutingKey"` → todos pasan.

### Commit WU-1

```
feat(ruteo-mp): agregar RutearSinEnriquecimiento y ExtraerRoutingKeyDesdeNotificacion al provider

- IWebhookProvider: dos métodos nuevos
- MercadoPagoProvider: implementación + helper privado ParsearRoutingKey (DRY)
- Tests P1-P13 (provider)
```

---

## WU-2 — Bifurcación en WebhookDispatcherWorker + extensión de FakeProviderForWorker

**Requisitos cubiertos**: REQ-1, REQ-2, REQ-3, REQ-4, REQ-5  
**Depende de**: WU-1 (IWebhookProvider con los dos métodos nuevos debe compilar)

### T-2.1 — Extender FakeProviderForWorker (preparación estructural)

- [ ] Archivo: `tests/Dotar.Gateway.Tests/Workers/WebhookDispatcherWorkerTests.cs` (línea ~828)
- Añadir a `FakeProviderForWorker`:
  - `bool RutearSinEnriquecimientoValor { get; set; } = false`
  - `RoutingKeyResult RoutingKeyDesdeNotificacionResult { get; set; } = RoutingKeyResult.Invalid`
  - `bool RutearSinEnriquecimientoLlamado { get; private set; }`
  - Implementar `RutearSinEnriquecimiento(string payload)` → registrar llamado + retornar valor.
  - Implementar `ExtraerRoutingKeyDesdeNotificacion(string payload)` → retornar resultado configurado.
- Verificación: `dotnet build src/Dotar.Gateway/Dotar.Gateway.csproj` compila sin errores.

### T-2.2 — Tests W1–W4: rama order en el worker (RED)

- [ ] Archivo: `tests/Dotar.Gateway.Tests/Workers/WebhookDispatcherWorkerTests.cs`
- Escenarios:
  - W1: `RutearSinEnriquecimientoValor=true` + `RoutingKeyDesdeNotificacionResult=Valido("CAJA-01")` + caja existe → forward RAW ocurre, `EnriquecimientoLlamado=false` (REQ-1 esc. 1, REQ-3 esc. 1)
  - W2: `RutearSinEnriquecimientoValor=true` + `RoutingKeyDesdeNotificacionResult=Invalid` → dead-letter, log Worker, sin forward (REQ-2 esc. 3/4/5)
  - W3: `RutearSinEnriquecimientoValor=true` + routing key válida + caja NO existe → dead-letter, log Worker (REQ-3 esc. 2)
  - W4: assert explícito `EnriquecimientoLlamado=false` cuando `RutearSinEnriquecimientoValor=true` (REQ-1 esc. 1, "sin llamada a MP API")
- Verificación: `dotnet test ... --filter "FullyQualifiedName~W1|W2|W3|W4"` → todos FAIL

### T-2.3 — Implementar bifurcación en ProcesarFlujoProveedorAsync (GREEN)

- [ ] Archivo: `src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs`
- Insertar después de resolver provider+config, antes de `ExtraerIdEvento`:
  - Declarar `RoutingKeyResult routingKeyResult;` antes del if.
  - Rama order: log Info Worker (tipo="order", modo="sin_enriquecimiento", id webhook) + `routingKeyResult = provider.ExtraerRoutingKeyDesdeNotificacion(webhook.Payload)`.
  - Rama else: flujo existente intacto (ExtraerIdEvento + EnriquecerAsync + ExtraerRoutingKey).
  - Tramo común posterior (chequeo !EsValido, buscar caja, sign, forward RAW, dead-letter): reusar sin tocar.
- Verificación: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` → W1–W4 pasan.

### T-2.4 — Tests W5–W6: no-regresión flujo payment (RED → GREEN)

- [ ] W5: `RutearSinEnriquecimientoValor=false` → `EnriquecimientoLlamado=true`, forward ocurre normal (REQ-4 esc. 1)
- [ ] W6: verificar que los tests de no-regresión payment existentes (caja no encontrada, routing key inválida, error enriquecimiento) siguen verdes tras la bifurcación. Si no existen, añadirlos.
- Verificación: `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` → suite completa verde.

### Commit WU-2

```
feat(ruteo-mp): bifurcación order/payment en WebhookDispatcherWorker

- ProcesarFlujoProveedorAsync: rama order salta enriquecimiento, usa ExtraerRoutingKeyDesdeNotificacion
- FakeProviderForWorker extendido con los dos métodos nuevos
- Tests W1-W6 (worker: order directo, dead-letters, no-regresión payment)
- Log Worker con tipo detectado y modo
```

---

## WU-3 — Actualizar contrato-boundary.md (sección B + bump versión)

**Requisitos cubiertos**: REQ-5 (contrato público), invariantes de spec  
**Paralelo lógico con WU-1/WU-2**, pero se commitea después de WU-2

### T-3.1 — Actualizar sección B de contrato-boundary.md

- [ ] Archivo: `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md`
- Cambios en sección B:
  - Reemplazar pasos 2-3 actuales por lógica condicional:
    - `type == "order"`: Gateway lee `data.external_reference` del RAW → Split("__",2)[0] → reenvía RAW firmado. Sin llamada a MP API.
    - `type == "payment"` (o ausente): Gateway llama `GET /v1/payments/{id}` → extrae `external_reference` raíz → reenvía RAW firmado.
  - Actualizar tabla de headers/body para clarificar que el body reenviado es siempre el RAW original (ambos tipos).
  - Actualizar diagrama D.4 para mostrar bifurcación type=order vs type=payment.
  - Bump versión en encabezado: `**Versión**: 1.0` → `**Versión**: 1.1`.
- Secciones A, C, D.1-3/5 y referencia rápida: sin cambios.

### Commit WU-3

```
docs(contrato-boundary): sección B condicional por tipo, bump 1.0→1.1

- type=order: routing key desde data.external_reference del RAW, sin enriquecer
- type=payment: flujo existente con GET /v1/payments
- Diagrama D.4 actualizado con bifurcación
```

---

## WU-4 — Verificación final

**Depende de**: WU-1, WU-2, WU-3

### T-4.1 — Build limpio

- [ ] `dotnet build src/Dotar.Gateway/Dotar.Gateway.csproj` → 0 errores, 0 warnings nuevos.

### T-4.2 — Suite completa verde

- [ ] `dotnet test tests/Dotar.Gateway.Tests/Dotar.Gateway.Tests.csproj` → todos los tests pasan.
- Confirmar cobertura: P1–P13 (provider) + W1–W6 (worker) + tests pre-existentes (MercadoPagoProviderTests, RegistroCajaEndpointsTests, DescifradoCredencialesWorkerTests).

### T-4.3 — Revisión de diff antes del PR

- [ ] `git diff --stat` → solo los 6 archivos esperados. Ningún archivo fuera de alcance.

---

## Orden de ejecución

```
WU-1 (provider: contratos + DRY)
  └─► WU-2 (worker: bifurcación + tests)
        └─► WU-3 (docs: contrato-boundary bump)
              └─► WU-4 (verificación final)
```

---

## Archivos afectados

| Archivo | Tipo de cambio |
|---------|---------------|
| `src/Dotar.Gateway/Providers/IWebhookProvider.cs` | +2 métodos en interfaz |
| `src/Dotar.Gateway/Providers/MercadoPagoProvider.cs` | +2 implementaciones + helper DRY |
| `src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs` | bifurcación en ProcesarFlujoProveedorAsync |
| `tests/Dotar.Gateway.Tests/Providers/MercadoPagoProviderTests.cs` | +13 tests |
| `tests/Dotar.Gateway.Tests/Workers/WebhookDispatcherWorkerTests.cs` | +6-7 tests + FakeProviderForWorker extendido |
| `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md` | sección B + bump 1.0→1.1 |
