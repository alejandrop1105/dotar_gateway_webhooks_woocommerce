# Propuesta — Ruteo de notificaciones MercadoPago `type=order` sin enriquecimiento

Las notificaciones reales de pago de MercadoPago Point llegan con `type=order` y traen un ID de orden (`ORD01KVX...`), no de pago. El worker hoy llama `GET /v1/payments/{idOrden}`, MP responde 404 y el webhook cae en dead-letter: el pago nunca llega al ERP del cliente. Esta propuesta hace que el worker rutee y reenvíe esas notificaciones leyendo `data.external_reference` directamente del payload ya firmado, sin la llamada de enriquecimiento que no aporta valor para este tipo.

## Camino rápido (qué cambia)

1. El worker detecta el tipo de notificación leyendo `type` del payload entrante.
2. Si `type == "order"`: extrae la routing key desde `data.external_reference` y reenvía el payload RAW firmado, sin llamar `EnriquecerAsync`.
3. Si `type == "payment"` o el campo está ausente: el flujo actual (enriquecimiento `GET /v1/payments/{id}`) queda INTACTO.
4. Se loguea en `SystemLog` el tipo detectado (order vs payment) para diagnóstico.

## Problema de negocio

- **Pain**: las notificaciones de pago de Point (`type=order`) caen en dead-letter de forma sistemática. El pago se cobra pero el ERP del cliente nunca lo recibe. Impacto productivo directo sobre la integración del cliente.
- **Causa raíz** (de la exploración #104): el worker pasa `data.id` (un ID de orden) a `GET /v1/payments/{id}`; MP devuelve 404 → `EnrichmentResult.Fallo` → dead-letter con motivo `error_enriquecimiento`. El payload de notificación ya contiene `data.external_reference` con el identificador de caja, por lo que el enriquecimiento es innecesario para `order`.
- **Por qué ahora**: la integración productiva del cliente es exclusivamente order/Point. Sin esto, no hay ruta funcional de pagos al ERP.

## Objetivo / outcome

Una notificación `order` firmada (x-signature ya validada en el endpoint) se rutea a la caja correcta y se reenvía al ERP con `X-Caja-Signature`, sin pasos innecesarios ni llamadas HTTP que fallan. El flujo `payment` clásico sigue funcionando sin cambios.

## Alcance

### Dentro (in-scope)

| Ítem | Detalle |
|------|---------|
| Bifurcación por tipo en el worker | En `ProcesarFlujoProveedorAsync`, mínima y localizada. Detecta `type` del payload. |
| Lectura de `data.external_reference` | Para el path `order`, leer el campo anidado bajo `data` (no la raíz). |
| Routing key anidada | `data.external_reference.Split("__", 2)[0]`. Mismas reglas de validación que hoy. |
| Reenvío RAW firmado | Reenviar el payload RAW de la notificación con `X-Caja-Signature` (HMAC del RAW), igual que hoy. |
| Logging del tipo | Registrar en `SystemLog` el tipo de notificación detectado (categoría `Worker`). |
| Actualización del contrato-boundary | Matizar la sección B de `openspec/specs/ruteo-webhooks-multitenant/contrato-boundary.md`: `type=order` se reenvía RAW sin enriquecimiento; `type=payment` mantiene el enriquecimiento. |
| Tests TDD | Path order feliz, sin `data.external_reference`, `external_reference` inválido, no-regresión `payment`. |

### Fuera (non-goals)

- Re-procesar dead-letters viejos de `order` (se aceptan como perdidos).
- Refactor del flujo `payment` / enriquecimiento clásico.
- Soportar otras APIs de MP (`/v1/orders`, etc.) — enfoque sin enriquecimiento, no enfoque B.
- UI nueva o cambios en endpoints públicos.
- Cambiar el formato del `external_reference`, del `X-Caja-Signature` o de la validación x-signature entrante.

## Enfoque técnico (alto nivel)

**Enfoque A — sin enriquecimiento** (decidido por el usuario; ver exploración #104):

- **Dónde**: la bifurcación vive en `WebhookDispatcherWorker.ProcesarFlujoProveedorAsync`, mínima y localizada. El worker lee `type` del payload entrante antes del paso de enriquecimiento.
- **Detección de `order`**: parseo del campo `type` del payload RAW. Si `type == "order"` → path sin enriquecimiento. Cualquier otro valor o ausencia → path actual (enriquecido).
- **Extracción de routing key anidada**: para `order`, leer `data.external_reference` (no la raíz). El `ExtraerRoutingKey` actual lee de la raíz del payload enriquecido (`/v1/payments`); el path `order` debe leer el campo anidado sin romper el path enriquecido. La forma concreta (método nuevo en el provider vs. lógica de lectura del campo anidado) se define en la fase de diseño.
- **Validación**: mismas reglas que hoy — sin `__` o parte izquierda vacía → dead-letter.
- **Reenvío**: payload RAW de la notificación firmado con `X-Caja-Signature` (HMAC-SHA256 del RAW con `WebhookSecret`), idéntico al flujo actual.
- **Seguridad**: la firma x-signature ya fue validada en el endpoint usando `data.id`, por lo que `data.external_reference` proviene de un payload firmado. No se requiere enriquecimiento para confiar en él.

### Casos de error para `order`

| Caso | Resultado |
|------|-----------|
| Sin `data.external_reference` | Dead-letter |
| `external_reference` sin separador `__` o parte izquierda vacía | Dead-letter |
| Caja no encontrada en el padrón | Dead-letter (igual que hoy) |

## Impacto y riesgos

| Riesgo | Mitigación |
|--------|------------|
| Regresión sobre el flujo `payment` | El path `payment` queda intacto; test de no-regresión obligatorio (`Worker_TipoPago_SigueFlujoActual`). |
| Inconsistencia del contrato-boundary | Actualizar la sección B para describir el comportamiento condicional por tipo. |
| Dead-letters productivos ya perdidos | Aceptado como non-goal; no se reprocesan. |
| Acoplamiento del worker a tipos de MP | La detección de `type` introduce conocimiento de MP en el worker. Se evalúa en diseño si conviene encapsular en el provider. Riesgo bajo: cambio localizado. |

## Estimación de tamaño y entrega

- **Tamaño estimado**: bajo. Bifurcación localizada en el worker + lectura de campo anidado + logging + actualización de un doc + ~4 tests. Estimado < 400 líneas.
- **Entrega**: un solo PR (delivery `ask-on-risk`; no se prevé necesidad de chained PRs).

## Próximo paso

`sdd-spec` y `sdd-design` (pueden correr en paralelo).
