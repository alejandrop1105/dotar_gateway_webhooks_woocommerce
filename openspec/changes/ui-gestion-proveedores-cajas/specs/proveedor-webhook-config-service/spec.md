# Delta para: proveedor-webhook-config-service

## ADDED Requirements

### Requirement: Listar configuraciones con hint enmascarado (sin credenciales en claro)

El servicio DEBE exponer un método que retorne la lista de todas las configuraciones de proveedor como DTOs de solo metadata, incluyendo un hint de credenciales computado server-side. El DTO de respuesta NO DEBE incluir accessToken ni signingSecret en ningún campo, ni exponer el ciphertext completo.

El hint DEBE computarse server-side descifrando el accessToken y extrayendo los últimos 6 caracteres, con el prefijo `••••••`. Si el descifrado falla, el hint DEBE ser `••••••??????`.

Firma requerida: `Task<List<ProveedorConfigMetadataDto>> ListarMetadataAsync()`

```
ProveedorConfigMetadataDto(
    long Id,
    int TenantId,
    string TenantNombre,       // join con Tenants
    string ProveedorNombre,
    string CuentaExternaId,
    string BaseUrl,
    bool IsActive,
    string HintCredenciales,   // formato ••••••XXXXXX
    DateTime CreatedAt,
    DateTime UpdatedAt
)
```

#### Scenario: Listado exitoso con registros

- GIVEN que existen configuraciones de proveedor en la base de datos
- WHEN se invoca `ListarMetadataAsync()`
- THEN retorna un `List<ProveedorConfigMetadataDto>` con una entrada por configuración
- AND cada DTO incluye HintCredenciales en formato `••••••XXXXXX`
- AND ningún DTO contiene accessToken, signingSecret ni CredencialesCifradas

#### Scenario: Hint cuando el accessToken tiene menos de 6 caracteres

- GIVEN una configuración cuyo accessToken descifrado tiene menos de 6 caracteres
- WHEN se computa el hint
- THEN el hint es `••••••` seguido de todos los caracteres del accessToken (sin truncar)

#### Scenario: Hint cuando el descifrado falla

- GIVEN una configuración cuyas CredencialesCifradas no pueden descifrarse (clave rotada, dato corrupto)
- WHEN se computa el hint
- THEN el hint es `••••••??????`
- AND el error se registra en el logger sin lanzar excepción al caller

#### Scenario: Listado vacío

- GIVEN que no existen configuraciones de proveedor
- WHEN se invoca `ListarMetadataAsync()`
- THEN retorna una lista vacía sin lanzar excepción

---

### Requirement: Eliminar configuración de proveedor por Id

El servicio DEBE exponer un método que elimine una configuración por su Id. Si el Id no existe, DEBE retornar un Result de error de tipo NotFound. La eliminación NO DEBE afectar las entidades CajaRegistrada ni el flujo de ingesta.

Firma requerida: `Task<Result> EliminarAsync(long id)`

#### Scenario: Eliminación exitosa

- GIVEN que existe una configuración con el Id proporcionado
- WHEN se invoca `EliminarAsync(id)`
- THEN el registro se elimina de la base de datos
- AND retorna `Result.Success()`

#### Scenario: Id no encontrado

- GIVEN que no existe ninguna configuración con el Id proporcionado
- WHEN se invoca `EliminarAsync(id)`
- THEN retorna `Result.Failure(ResultError.NotFound, ...)`
- AND no se lanza excepción

---

### Requirement: No modificar firmas ni comportamiento de métodos existentes

El servicio NO DEBE modificar las firmas, comportamientos ni efectos de: `UpsertAsync`, `GetByProveedorYCuentaAsync`, `GetByTenantYProveedorAsync`, `GetCompletoByProveedorYCuentaAsync`. El flujo de ruteo de webhooks (ingesta 1-a-1) DEBE permanecer intacto.

#### Scenario: Métodos existentes no alterados

- GIVEN el servicio con los nuevos métodos añadidos
- WHEN se invoca cualquier método preexistente con los mismos argumentos que antes del cambio
- THEN el comportamiento y los valores de retorno son idénticos a los anteriores al cambio
- AND ningún test de integración del flujo de ingesta falla
