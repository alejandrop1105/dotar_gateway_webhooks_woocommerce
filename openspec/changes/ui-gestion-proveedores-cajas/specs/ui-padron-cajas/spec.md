# Especificación: ui-padron-cajas

## Propósito

Página `/cajas` (Blazor Server, MudBlazor v9) que permite visualizar el padrón de cajas registradas por tenant y revocar/eliminar registros, con advertencia cuando el heartbeat reciente indica que la caja podría volver a registrarse automáticamente.

---

## Requirements

### Requirement: Listado de cajas por tenant

El sistema DEBE mostrar en `/cajas` la lista de cajas registradas filtrables por tenant, con las columnas: identificador, callbackUrl, UltimaVez (heartbeat), createdAt y acciones.

#### Scenario: Listado con cajas existentes para un tenant

- GIVEN que existen cajas registradas para uno o más tenants
- WHEN el usuario navega a `/cajas` y selecciona un tenant en el filtro
- THEN la tabla muestra todas las cajas de ese tenant con identificador, callbackUrl, UltimaVez y createdAt
- AND las cajas se ordenan por UltimaVez descendente (más reciente primero)

#### Scenario: Listado sin filtro de tenant

- GIVEN que existen cajas de múltiples tenants
- WHEN el usuario navega a `/cajas` sin seleccionar filtro de tenant
- THEN la tabla muestra todas las cajas de todos los tenants
- AND cada fila indica a qué tenant pertenece

#### Scenario: Estado vacío — tenant sin cajas

- GIVEN un tenant seleccionado que no tiene cajas registradas
- WHEN el sistema aplica el filtro
- THEN se muestra un estado vacío con mensaje informativo
- AND no se muestra error de ejecución

---

### Requirement: Filtrar cajas por tenant

El sistema DEBE proveer un selector de tenant que filtre la tabla en tiempo de renderizado (o al cambiar selección), mostrando solo las cajas del tenant elegido.

#### Scenario: Filtrado por tenant específico

- GIVEN que existen cajas de múltiples tenants
- WHEN el usuario selecciona un tenant en el selector
- THEN la tabla muestra únicamente las cajas de ese tenant
- AND el conteo visible coincide con el total de cajas del tenant

#### Scenario: Limpiar filtro

- GIVEN el usuario tiene un tenant seleccionado
- WHEN limpia el filtro (selecciona "Todos")
- THEN la tabla vuelve a mostrar cajas de todos los tenants

---

### Requirement: Revocar/eliminar caja con advertencia por heartbeat reciente

El sistema DEBE permitir revocar (eliminar del padrón) una caja mediante ConfirmDialog. Si `UltimaVez` es más reciente que 10 minutos antes del momento de la acción, el ConfirmDialog DEBE incluir una advertencia explícita indicando que la caja podría volver a registrarse automáticamente en el próximo ciclo del ERP.

#### Scenario: Revocación con heartbeat reciente (advertencia)

- GIVEN una caja cuya UltimaVez es menor a 10 minutos antes del momento actual
- WHEN el usuario selecciona "Revocar" en la fila
- THEN el ConfirmDialog muestra la advertencia: "Esta caja tuvo actividad reciente y podría volver a registrarse automáticamente."
- AND el usuario puede confirmar o cancelar

#### Scenario: Revocación con heartbeat no reciente (sin advertencia)

- GIVEN una caja cuya UltimaVez es igual o mayor a 10 minutos antes del momento actual, o UltimaVez es null
- WHEN el usuario selecciona "Revocar" en la fila
- THEN el ConfirmDialog muestra confirmación estándar sin advertencia de heartbeat

#### Scenario: Revocación confirmada

- GIVEN el usuario confirma la revocación en el ConfirmDialog
- WHEN el sistema procesa la acción
- THEN la caja se elimina del padrón (invoca `RevocarAsync` del AppService)
- AND la fila desaparece de la tabla
- AND muestra snackbar de éxito

#### Scenario: Revocación cancelada

- GIVEN el usuario cancela la revocación en el ConfirmDialog
- WHEN descarta el dialog
- THEN la caja permanece en el padrón sin cambios

---

### Requirement: Scope de datos corto por operación

El sistema DEBE obtener el `GatewayDbContext` mediante `IServiceScopeFactory.CreateScope()` por operación, sin mantener un DbContext circuit-scoped en el componente Blazor de cajas.

#### Scenario: No hay DbContext circuit-scoped en el componente

- GIVEN el componente Blazor de cajas
- WHEN se ejecuta cualquier operación (listar, revocar)
- THEN se crea un scope nuevo vía IServiceScopeFactory
- AND el scope se libera al finalizar la operación
- AND no existe inyección de DbContext en el constructor del componente
