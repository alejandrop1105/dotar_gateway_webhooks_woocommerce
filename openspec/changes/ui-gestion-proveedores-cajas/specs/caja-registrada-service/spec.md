# Delta para: caja-registrada-service

## ADDED Requirements

### Requirement: Listar cajas por tenant

El servicio DEBE exponer un método que retorne la lista de cajas registradas para un tenant dado. Cuando `tenantId` es `null`, DEBE retornar todas las cajas de todos los tenants.

Firma requerida: `Task<List<CajaDto>> ListarPorTenantAsync(int? tenantId)`

El tipo `CajaDto` ya existe en el AppService (`CajaDto(long Id, int TenantId, string Identificador, string CallbackUrl, DateTime? UltimaVez, DateTime CreatedAt, DateTime UpdatedAt)`). No se introduce un tipo nuevo.

#### Scenario: Listado de cajas de un tenant específico

- GIVEN que existen cajas registradas para el tenant con id X
- WHEN se invoca `ListarPorTenantAsync(X)`
- THEN retorna una lista con todas las cajas cuyo TenantId == X
- AND cada elemento es un CajaDto sin campos de infraestructura interna

#### Scenario: Listado de todas las cajas (tenantId null)

- GIVEN que existen cajas de múltiples tenants
- WHEN se invoca `ListarPorTenantAsync(null)`
- THEN retorna una lista con todas las cajas de todos los tenants

#### Scenario: Tenant sin cajas

- GIVEN un tenantId válido sin cajas registradas
- WHEN se invoca `ListarPorTenantAsync(tenantId)`
- THEN retorna una lista vacía sin lanzar excepción

---

### Requirement: Revocar caja por Id

El servicio DEBE exponer un método que elimine una caja del padrón por su Id. Si el Id no existe, DEBE retornar un Result de error de tipo NotFound. La revocación NO DEBE afectar el flujo de auto-registro (`RegistrarAsync`) ni invalidar la caché de forma que rompa el ruteo activo de otras cajas del mismo tenant.

Firma requerida: `Task<Result> RevocarAsync(long id)`

#### Scenario: Revocación exitosa

- GIVEN que existe una caja con el Id proporcionado
- WHEN se invoca `RevocarAsync(id)`
- THEN el registro se elimina de la base de datos
- AND la caché para (TenantId, Identificador) de esa caja se invalida
- AND retorna `Result.Success()`

#### Scenario: Id no encontrado

- GIVEN que no existe ninguna caja con el Id proporcionado
- WHEN se invoca `RevocarAsync(id)`
- THEN retorna `Result.Failure(ResultError.NotFound, ...)`
- AND no se lanza excepción
- AND no se modifica la caché de otras cajas

#### Scenario: La revocación no afecta el ruteo de otras cajas del mismo tenant

- GIVEN que el tenant tiene múltiples cajas registradas
- WHEN se revoca una de ellas con `RevocarAsync(id)`
- THEN las demás cajas del tenant siguen resolviendo correctamente via caché/DB
- AND RegistrarAsync puede registrar nuevamente esa caja en el próximo ciclo del ERP

---

### Requirement: No modificar firmas ni comportamiento de métodos existentes

El servicio NO DEBE modificar la firma, comportamiento, validaciones anti-SSRF, allowlist de dominios ni efectos de `RegistrarAsync`. El flujo de auto-registro de cajas desde el ERP DEBE permanecer intacto.

#### Scenario: RegistrarAsync no alterado

- GIVEN el servicio con los nuevos métodos añadidos
- WHEN se invoca `RegistrarAsync(tenantId, identificador, callbackUrl)` con argumentos válidos
- THEN el comportamiento, validaciones y valores de retorno son idénticos a los anteriores al cambio
- AND las validaciones anti-SSRF (https, puerto 443, allowlist) siguen activas
