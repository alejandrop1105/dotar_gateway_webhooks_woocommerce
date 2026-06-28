# Proposal: Exponer configuración de ruteo multi-sucursal del Tenant (UI + API)

## Intent

El change `proveedor-woocommerce-multisucursal` (PRs #11/#12) agregó 4 columnas a `Tenant`
(`RuteoProveedorActivo`, `ProveedorRuteoNombre`, `SucursalMetaKey`, `SucursalMetaSeparador`),
pero NO hay forma de editarlas: ni en el form `/tenants` ni en la API REST de tenants. Hoy
solo se setean con un `UPDATE` directo a la DB. El feature de ruteo es inutilizable en la
práctica. Este change expone esa configuración por las DOS vías de administración existentes:
el formulario Blazor y la API REST (que el ERP del usuario YA consume para registrar tenants).

## Scope

### In Scope
- Agregar los 4 campos OPCIONALES a `CreateTenantRequest` / `UpdateTenantRequest` y a los
  inputs de Application (`CreateTenantInput` / `UpdateTenantInput`).
- Validación de negocio COMPARTIDA en `TenantAppService` (Create y Update), respetada por UI y API.
- Validar `ProveedorRuteoNombre` contra las keys reales del keyed DI (`mercadopago`, `woocommerce-multisucursal`).
- Semántica de apagado/limpieza coherente con las convenciones parciales existentes.
- Exponer los 4 campos en el form `/tenants` con validación condicional.

### Out of Scope
- La lógica de ruteo en sí (ya implementada en `proveedor-woocommerce-multisucursal`).
- Extracción anidada en `shipping_lines` (mejora futura).
- Gestión de cajas/sucursales (ya existe vía `/registro-caja`).
- Auth nueva para la API (se reusa la API Key existente, header `X-Gateway-Api-Key`).

## Capabilities

### New Capabilities
- None

### Modified Capabilities
- `tenant-management`: la administración de tenants (UI + API) ahora debe aceptar, validar y
  persistir la configuración de ruteo multi-sucursal con validación de negocio compartida.

## Approach

Tratar los 4 campos como una extensión de las convenciones de "actualización parcial" ya
presentes en `TenantAppService`/`TenantApiEndpoints`:
- DTOs e inputs reciben los campos como nullable con default → en Create ausentes = defaults
  (`RuteoProveedorActivo=false`); en Update null = no se tocan.
- Una única regla de validación en `TenantAppService` (usada por ambos `CreateAsync`/`UpdateAsync`)
  evita duplicar lógica entre UI y API.
- `ProveedorRuteoNombre` se valida contra las keys registradas en keyed DI (origen: `IWebhookProvider.Nombre`),
  no texto libre, espejando el patrón de validación de FKs existente.

## Business Rules (v1)

1. **Compatibilidad hacia atrás (CRÍTICO)**: los 4 campos son OPCIONALES en Create y Update.
   El ERP que hoy llama sin ellos NO debe romperse. Create sin campos → defaults; Update null → sin cambio.
2. **Validación compartida**: si `RuteoProveedorActivo=true`, entonces `ProveedorRuteoNombre`
   y `SucursalMetaKey` son OBLIGATORIOS. `SucursalMetaSeparador` siempre opcional.
3. **Provider válido**: `ProveedorRuteoNombre` debe coincidir con una key registrada (`mercadopago`,
   `woocommerce-multisucursal`). Inválido → error de validación claro.
4. **Apagado/limpieza**: `RuteoProveedorActivo=false` desactiva el ruteo; al apagarlo se limpian
   los campos dependientes de forma coherente con las convenciones parciales existentes.
5. **UI espeja la validación**: el form `/tenants` expone los 4 campos; la key solo es requerida
   cuando el modo está activo.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Dotar.Gateway/Endpoints/TenantApiEndpoints.cs` | Modified | +4 campos en DTOs y mapeo a inputs |
| `src/Dotar.Gateway/Application/TenantAppService.cs` | Modified | +4 campos en inputs; validación compartida |
| `src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor` | Modified | Form con campos + validación condicional |
| `src/Dotar.Gateway/Program.cs` | Read | Origen de keys válidas (keyed DI) |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Romper contrato API del ERP | High | Campos opcionales + defaults; cubrir con test de "request sin campos nuevos" |
| Duplicar validación UI vs API | Med | Centralizar la regla en `TenantAppService`; UI consume el mismo servicio |
| `ProveedorRuteoNombre` desincronizado con keys reales | Med | Validar contra keys del keyed DI, no lista hardcodeada en strings dispersos |

## Rollback Plan

Cambio aditivo y sin migración (columnas ya existen desde PR #11). Revertir = revertir el PR;
los tenants ya configurados conservan sus valores en DB. No requiere `down` de migración.

## Dependencies

- DEPENDE de las columnas de `Tenant` del change `proveedor-woocommerce-multisucursal` (PR #11).
  El apply debe hacerse sobre una base que ya las tenga (idealmente tras mergear #11). Verificado:
  migración `20260626141012_AgregarRuteoProveedorWooCommerceMultiSucursal` y propiedades en
  `Domain/Entities/Tenant.cs` ya presentes en el repo.

## Success Criteria

- [ ] Crear/actualizar tenant vía API SIN los campos nuevos sigue funcionando (compat).
- [ ] `RuteoProveedorActivo=true` sin `ProveedorRuteoNombre` o sin `SucursalMetaKey` → 400 con mensaje claro.
- [ ] `ProveedorRuteoNombre` inválido → 400 con mensaje claro.
- [ ] Update parcial puede apagar el ruteo y limpiar campos dependientes.
- [ ] Form `/tenants` permite configurar los 4 campos con validación condicional.
- [ ] La misma regla de validación aplica idéntica desde UI y API.

## Edge Cases

- `RuteoProveedorActivo=true` sin `SucursalMetaKey` → rechazar.
- `ProveedorRuteoNombre` con valor no registrado → rechazar.
- Update parcial que pasa `RuteoProveedorActivo=false` → desactivar y limpiar dependientes.
- ERP que no envía ninguno de los 4 campos → comportamiento idéntico al actual.
