# Especificación: ui-config-proveedor

## Propósito

Página `/proveedores` (Blazor Server, MudBlazor v9) que permite gestionar visualmente las configuraciones de ProveedorWebhookConfig: listar, crear, editar metadata, cambiar credenciales de forma separada, activar/desactivar y eliminar. Las credenciales completas NUNCA alcanzan el cliente.

---

## Requirements

### Requirement: Listado de configuraciones de proveedor con hint enmascarado

El sistema DEBE renderizar en `/proveedores` una tabla con todas las configuraciones existentes mostrando: tenant (nombre), proveedorNombre, cuentaExternaId, baseUrl, isActive, createdAt, updatedAt y un hint de credenciales en formato `••••••XXXXXX` (últimos 6 caracteres).

El sistema NO DEBE incluir accessToken ni signingSecret en ningún modelo, DTO de respuesta HTTP, campo de formulario ni estado del componente Blazor que pueda alcanzar el cliente en texto legible.

#### Scenario: Listado con registros existentes

- GIVEN que existen una o más configuraciones de proveedor en la base de datos
- WHEN el usuario navega a `/proveedores`
- THEN la tabla muestra una fila por configuración con: tenant, proveedorNombre, cuentaExternaId, baseUrl, badge isActive, createdAt, updatedAt y hint enmascarado (`••••••XXXXXX`)
- AND el hint muestra los últimos 6 caracteres del accessToken cifrado, precedidos por `••••••`

#### Scenario: Hint nunca expone el valor completo

- GIVEN una configuración con accessToken de cualquier longitud
- WHEN el sistema computa el hint
- THEN el hint tiene exactamente el formato `••••••` + últimos 6 chars del accessToken
- AND el accessToken completo no aparece en el DOM, en ningún DTO de respuesta, ni en logs del navegador

#### Scenario: Listado vacío

- GIVEN que no existen configuraciones de proveedor
- WHEN el usuario navega a `/proveedores`
- THEN la tabla muestra un estado vacío con mensaje informativo
- AND no se renderiza ningún error de ejecución

---

### Requirement: Crear configuración de proveedor

El sistema DEBE permitir crear una nueva configuración mediante un formulario que incluya: tenant (selector), proveedorNombre, cuentaExternaId, baseUrl, isActive, accessToken y signingSecret. La creación invoca `ProveedorWebhookConfigAppService.UpsertAsync`.

#### Scenario: Alta exitosa

- GIVEN el usuario completa todos los campos obligatorios con valores válidos
- WHEN envía el formulario de alta
- THEN el sistema persiste la configuración con credenciales cifradas
- AND muestra snackbar de éxito
- AND la nueva fila aparece en la tabla sin credenciales en claro

#### Scenario: Alta con campos obligatorios vacíos

- GIVEN el usuario deja vacío proveedorNombre, cuentaExternaId o credenciales
- WHEN intenta enviar el formulario
- THEN el sistema muestra validación en el campo correspondiente
- AND no llama a UpsertAsync

#### Scenario: Alta con upsert (registro existente)

- GIVEN ya existe una configuración para el mismo par (TenantId, proveedorNombre)
- WHEN el usuario crea una con los mismos valores
- THEN UpsertAsync actualiza el registro existente (idempotente)
- AND el sistema lo indica con el mismo snackbar de éxito

---

### Requirement: Editar metadata de configuración (sin credenciales)

El sistema DEBE permitir editar cuentaExternaId, baseUrl e isActive de una configuración existente sin requerir ni mostrar accessToken/signingSecret en el formulario principal de edición. La edición invoca `UpsertAsync` reutilizando las credenciales cifradas ya almacenadas.

#### Scenario: Edición de metadata exitosa

- GIVEN una configuración existente
- WHEN el usuario edita cuentaExternaId, baseUrl o isActive y guarda
- THEN el sistema actualiza solo esos campos
- AND las credenciales cifradas permanecen intactas (no se sobrescriben)
- AND muestra snackbar de éxito

#### Scenario: Formulario de edición no incluye campos de credenciales

- GIVEN el usuario abre el formulario de edición de una configuración
- WHEN el sistema renderiza el formulario
- THEN los campos accessToken y signingSecret NO están presentes en el formulario principal
- AND el hint enmascarado es visible como referencia informativa (solo lectura)

---

### Requirement: Dialog separado "Cambiar credenciales"

El sistema DEBE proveer un dialog independiente accesible desde la fila de la tabla que permita reingresar accessToken y signingSecret. Al confirmar, invoca `UpsertAsync` con las credenciales nuevas y la metadata existente. El dialog no pre-rellena los campos con valores actuales.

#### Scenario: Cambio de credenciales exitoso

- GIVEN el usuario abre el dialog "Cambiar credenciales" de una configuración
- WHEN ingresa nuevos accessToken y signingSecret válidos y confirma
- THEN el sistema cifra y persiste las nuevas credenciales
- AND el hint en la tabla se actualiza a los últimos 6 chars del nuevo accessToken
- AND muestra snackbar de éxito

#### Scenario: Dialog no pre-rellena credenciales

- GIVEN el usuario abre el dialog "Cambiar credenciales"
- WHEN el sistema renderiza el dialog
- THEN los campos accessToken y signingSecret aparecen vacíos
- AND el hint actual se muestra como referencia de solo lectura

#### Scenario: Cancelar sin cambios

- GIVEN el usuario abre el dialog "Cambiar credenciales" y cancela sin guardar
- WHEN descarta el dialog
- THEN las credenciales almacenadas permanecen sin cambios

---

### Requirement: Activar/desactivar configuración (toggle isActive)

El sistema DEBE permitir activar o desactivar una configuración directamente desde la tabla mediante un toggle o acción rápida, sin abrir el formulario completo.

#### Scenario: Desactivar configuración activa

- GIVEN una configuración con isActive = true
- WHEN el usuario activa el toggle de la fila
- THEN el sistema persiste isActive = false
- AND el badge de la fila refleja el nuevo estado
- AND muestra snackbar de confirmación

#### Scenario: Activar configuración inactiva

- GIVEN una configuración con isActive = false
- WHEN el usuario activa el toggle de la fila
- THEN el sistema persiste isActive = true
- AND el badge de la fila refleja el nuevo estado

---

### Requirement: Eliminar configuración de proveedor

El sistema DEBE permitir eliminar una configuración de proveedor desde la tabla, previa confirmación mediante ConfirmDialog. La eliminación invoca el nuevo método `EliminarAsync` del AppService (ver spec `proveedor-webhook-config-service`).

#### Scenario: Borrado con confirmación

- GIVEN una configuración existente
- WHEN el usuario selecciona eliminar y confirma en el ConfirmDialog
- THEN el sistema elimina el registro
- AND la fila desaparece de la tabla
- AND muestra snackbar de éxito

#### Scenario: Borrado cancelado

- GIVEN el usuario selecciona eliminar y cancela en el ConfirmDialog
- WHEN descarta el dialog
- THEN el registro permanece en la tabla sin cambios

---

### Requirement: Acceso desde fila de tenant en /tenants

El sistema DEBE incluir en la fila de cada tenant en la página `/tenants` una acción que navegue a `/proveedores` filtrada por ese tenant, o abra el formulario de alta de config de proveedor para ese tenant.

#### Scenario: Acceso a config de proveedor desde tenant

- GIVEN el usuario visualiza la lista de tenants en `/tenants`
- WHEN selecciona la acción "Ver/Agregar config de proveedor" en una fila de tenant
- THEN el sistema navega a `/proveedores` con el tenant pre-seleccionado como filtro activo

---

### Requirement: Scope de datos corto por operación

El sistema DEBE obtener el `GatewayDbContext` mediante `IServiceScopeFactory.CreateScope()` por operación en el componente Blazor, sin mantener un DbContext circuit-scoped.

#### Scenario: No hay DbContext circuit-scoped en el componente

- GIVEN el componente Blazor de proveedores
- WHEN se ejecuta cualquier operación (listar, crear, editar, eliminar)
- THEN se crea un scope nuevo vía IServiceScopeFactory
- AND el scope se libera al finalizar la operación (using / await using)
- AND no existe inyección de DbContext en el constructor del componente
