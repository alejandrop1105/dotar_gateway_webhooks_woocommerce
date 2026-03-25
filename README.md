# 🚀 Dotar Gateway — Webhook Gateway Multi-Tenant para WooCommerce

<div align="center">

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet)
![MudBlazor](https://img.shields.io/badge/MudBlazor-v8-7B1FA2?style=for-the-badge)
![Blazor Server](https://img.shields.io/badge/Blazor-Server-512BD4?style=for-the-badge&logo=blazor)
![SQLite](https://img.shields.io/badge/SQLite-003B57?style=for-the-badge&logo=sqlite)
![Redis](https://img.shields.io/badge/Redis-DC382D?style=for-the-badge&logo=redis)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker)
![Cloudflare](https://img.shields.io/badge/Cloudflare-F38020?style=for-the-badge&logo=cloudflare)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**Gateway intermediario de alto rendimiento que recibe, valida, encola y reenvía webhooks de WooCommerce con políticas de reintento avanzadas, monitoreo en tiempo real, administración grupal de tenants y túnel seguro vía Cloudflare.**

</div>

---

## 📋 Tabla de Contenidos

- [¿Qué es Dotar Gateway?](#-qué-es-dotar-gateway)
- [Arquitectura](#-arquitectura)
- [Características](#-características)
- [Requisitos](#-requisitos)
- [Instalación Rápida (Docker)](#-instalación-rápida-docker)
- [Instalación Manual](#-instalación-manual)
- [Configuración](#-configuración)
- [Uso](#-uso)
- [Dashboard](#-dashboard)
- [API Endpoints](#-api-endpoints)
- [Políticas de Reintento](#-políticas-de-reintento)
- [Túnel Cloudflare](#-túnel-cloudflare)
- [Estructura del Proyecto](#-estructura-del-proyecto)
- [Modelo de Datos](#-modelo-de-datos)
- [Seguridad](#-seguridad)
- [Troubleshooting](#-troubleshooting)

---

## 🔍 ¿Qué es Dotar Gateway?

Dotar Gateway es un **servicio intermediario** que se posiciona entre WooCommerce y tu sistema de destino (ERP, CRM, API interna, etc.). En vez de que WooCommerce envíe los webhooks directamente a tu sistema, los envía al Gateway, que se encarga de:

1. **Recibir** el webhook vía HTTPS
2. **Validar** la firma HMAC-SHA256 (autenticidad del origen)
3. **Encolar** el payload en Redis (desacople producción/consumo)
4. **Reenviar** al destino final con reintentos inteligentes
5. **Registrar** cada intento de entrega con código HTTP, duración y descripción de error
6. **Monitorear** cada entrega en un dashboard en tiempo real con acciones masivas

### ¿Por qué un Gateway?

| Problema                                                    | Solución del Gateway                                            |
| ----------------------------------------------------------- | --------------------------------------------------------------- |
| WooCommerce tiene reintentos limitados (max 5, sin control) | Políticas configurables por paso (segundos → horas → días)      |
| Si tu API está caída, perdés el webhook                     | Cola Redis + scheduler que reintenta automáticamente            |
| No hay visibilidad de qué llegó y qué falló                 | Dashboard con historial de intentos, payload, y stats           |
| Múltiples tiendas WooCommerce = múltiples configs           | Un solo Gateway maneja N tenants organizados en grupos          |
| No hay trazabilidad de cada intento                         | Historial detallado por intento con HTTP status, error y origen |
| WooCommerce no valida errores SSL/timeout correctamente     | Circuit breaker + manejo robusto de errores                     |

---

## 🏗️ Arquitectura

```
┌─────────────┐    HTTPS     ┌──────────────────────────────────────────┐
│ WooCommerce │──────────────▶│           DOTAR GATEWAY                 │
│  Tienda A   │  /ingest/a   │                                          │
└─────────────┘              │  ┌────────┐  ┌───────┐  ┌────────────┐  │
                             │  │ Ingest │─▶│ Redis │─▶│   Worker   │  │
┌─────────────┐  /ingest/b   │  │Endpoint│  │ Queue │  │ Dispatcher │──┼──▶ API Destino A
│ WooCommerce │──────────────▶│  └────────┘  └───────┘  └────────────┘  │
│  Tienda B   │              │       │                       │          │──▶ API Destino B
└─────────────┘              │  ┌────────┐          ┌────────────────┐  │
                             │  │  HMAC  │          │  Retry         │  │──▶ API Destino C
┌─────────────┐  /ingest/c   │  │Validate│          │  Scheduler     │  │
│ WooCommerce │──────────────▶│  └────────┘          │  (NextRetryAt) │  │
│  Tienda C   │              │                       └────────────────┘  │
└─────────────┘              │                                          │
                             │  ┌──────────────────────────────────────┐│
                             │  │        Blazor Dashboard              ││
                             │  │  Home │ Tenants │ Grupos │ Políticas ││
                             │  │  Monitor │ Detalle │ Configuración   ││
                             │  └──────────────────────────────────────┘│
                             └──────────────────────────────────────────┘
                                          ▲
                                          │ Cloudflare Tunnel
                                          │ (HTTPS automático)
```

---

## ✨ Características

### 🔀 Multi-Tenant con Grupos

- Múltiples tiendas WooCommerce en un solo Gateway
- Cada tenant con su propia URL destino, secret HMAC y política de reintento
- **Agrupación lógica** de tenants en grupos (ej: "Cliente 1") para administración centralizada
- Políticas de reintento heredables: se pueden asignar a nivel de grupo y los tenants las heredan
- Slug único por tenant (ej: `/ingest/mi-tienda`)

### 🔄 Políticas de Reintento Avanzadas

- **Pasos configurables**: cada paso define su propio delay (segundos, minutos, horas, días)
- **Ejemplo**: `5s → 30s → 2min → 15min → 1h → 24h` (último intento al otro día)
- **Scheduler automático**: un background loop busca webhooks pendientes cada 5 segundos
- **Próximo reintento visible**: cada mensaje pendiente muestra la hora exacta del siguiente reintento
- **Circuit breaker**: protección contra destinos caídos (Polly)
- **Política por defecto**: se incluye una política "Estándar" predefinida

### 📡 Monitoreo en Tiempo Real

- **Dashboard Blazor Server** con auto-refresh
- **Historial de intentos** por webhook — cada intento registra:
  - Número de intento secuencial
  - Código HTTP de respuesta
  - Duración en milisegundos
  - Descripción del error (cuando falla)
  - Origen: automático o manual
  - Fecha y hora exacta
- **Stats**: tasa de éxito, fallidos, en cola, programados
- **Acciones**: reenvío manual (genera un nuevo intento), reenvío masivo, eliminación individual/masiva
- **Copiar payload**: botón para copiar el payload JSON al portapapeles

### 🎨 Dashboard UI (MudBlazor v8)

- **MudBlazor v8** — Componentes Material Design nativos (MudDataGrid, MudTimeline, MudTextField, etc.)
- **Tema dark custom** con paleta personalizada (`#6c63ff` primary, `#10b981` success, `#ef4444` error)
- **MudDrawer colapsable** con hamburger nativo en MudAppBar
- **Iconos Material Design** SVG nativos en sidebar y acciones
- **Indicador de túnel** en la barra superior (MudChip con estado de conexión Cloudflare)
- **Responsive** y optimizado para uso continuo

### 🔒 Seguridad

- **Validación HMAC-SHA256** de cada webhook entrante
- **Secrets por tenant** (cada WooCommerce tiene su propio secret)
- **Cloudflare Tunnel** para HTTPS automático sin exponer puertos
- **Sin credenciales hardcodeadas**: todo se configura vía dashboard o variables de entorno

### ⚡ Alto Rendimiento

- **Redis** como cola de mensajes (desacople producción/consumo)
- **SQLite con WAL mode** para escrituras concurrentes
- **Worker async** con procesamiento paralelo
- **Pipeline cacheado** por política (Polly)

---

## 📦 Requisitos

### Para Docker (recomendado)

| Componente       | Versión | Obligatorio |
| ---------------- | ------- | ----------- |
| Docker           | 20.10+  | ✅           |
| Docker Compose   | 2.0+    | ✅           |

### Para ejecución manual

| Componente         | Versión               | Obligatorio |
| ------------------ | --------------------- | ----------- |
| .NET SDK           | 9.0+                  | ✅           |
| Redis              | 6.0+                  | ✅           |
| SQLite             | (incluido en EF Core) | ✅           |
| Cloudflare Account | (para túnel HTTPS)    | Opcional    |
| `cloudflared` CLI  | Última versión        | Opcional    |

---

## 🐳 Instalación Rápida (Docker)

### 1. Clonar el repositorio

```bash
git clone https://github.com/alejandrop1105/dotar-gateway.git
cd dotar-gateway
```

### 2. Levantar con Docker Compose

```bash
docker-compose up -d --build
```

Esto levanta dos contenedores:
- **gateway-app**: La aplicación Dotar Gateway (puerto `8082`)
- **gateway-redis**: Redis 7 Alpine con persistencia AOF (puerto interno `6379`, externo `6380`)

### 3. Acceder al Dashboard

Abrí el navegador en [http://localhost:8082](http://localhost:8082)

### Detener el servicio

```bash
docker-compose down
```

### Ver logs en tiempo real

```bash
docker-compose logs -f gateway-app
```

### Reconstruir después de cambios

```bash
docker-compose up -d --build
```

---

## 🔧 Instalación Manual

### 1. Clonar y restaurar

```bash
git clone https://github.com/alejandrop1105/dotar-gateway.git
cd dotar-gateway
dotnet restore src/Dotar.Gateway/Dotar.Gateway.csproj
```

### 2. Configurar Redis

```bash
# Opción A: Docker
docker run -d --name redis -p 6379:6379 redis:alpine

# Opción B: Instalar nativamente
# Linux: sudo apt install redis-server
# Windows: https://github.com/microsoftarchive/redis/releases
```

### 3. Ejecutar

```bash
dotnet run --project src/Dotar.Gateway/Dotar.Gateway.csproj
```

El Gateway estará disponible en `http://localhost:5200`.

---

## ⚙️ Configuración

### Docker Compose (recomendado)

Las variables se configuran en `docker-compose.yml`:

```yaml
environment:
  - ASPNETCORE_ENVIRONMENT=Production
  - ConnectionStrings__Sqlite=Data Source=/app/data/gateway.db
  - ConnectionStrings__Redis=gateway-redis:6379
  - TZ=America/Argentina/Buenos_Aires
```

### `appsettings.json` (ejecución manual)

```json
{
  "ConnectionStrings": {
    "Sqlite": "Data Source=gateway.db",
    "Redis": "localhost:6379"
  },
  "Gateway": {
    "QueueName": "webhooks:incoming"
  }
}
```

### Variables de Entorno (alternativas)

```bash
ConnectionStrings__Sqlite=Data Source=gateway.db
ConnectionStrings__Redis=localhost:6379
```

### Volúmenes Docker

| Volumen              | Descripción                          |
| -------------------- | ------------------------------------ |
| `gateway-app-data`   | Base de datos SQLite persistente     |
| `gateway-redis-data` | Datos de Redis con persistencia AOF  |

### Puertos

| Puerto Externo | Puerto Interno | Servicio    |
| -------------- | -------------- | ----------- |
| `8082`         | `5200`         | Gateway App |
| `6380`         | `6379`         | Redis       |

---

## 🚀 Uso

### Paso 1: Crear un Grupo (opcional)

Desde el Dashboard → **Grupos** → **Nuevo Grupo**:

| Campo              | Descripción                              | Ejemplo        |
| ------------------ | ---------------------------------------- | -------------- |
| Nombre             | Nombre del grupo/cliente                 | `Cliente ABC`  |
| Política Reintento | Política que heredan los tenants del grupo | `Estándar`    |

### Paso 2: Crear un Tenant

Desde el Dashboard → **Tenants** → **Nuevo Tenant**:

| Campo          | Descripción                       | Ejemplo                       |
| -------------- | --------------------------------- | ----------------------------- |
| Nombre         | Nombre descriptivo de la tienda   | `Tienda Principal`            |
| Slug           | Identificador único en la URL     | `tienda-principal`            |
| URL Destino    | A dónde reenviar los webhooks     | `https://mi-api.com/webhooks` |
| Webhook Secret | Secret de WooCommerce (para HMAC) | `cs_abc123...`                |
| Grupo          | Grupo al que pertenece (opcional) | `Cliente ABC`                 |
| Política       | Política de reintento a usar      | `Estándar` (o heredar del grupo) |

### Paso 3: Configurar WooCommerce

En tu WooCommerce → **Settings → Advanced → Webhooks** → **Add Webhook**:

| Campo        | Valor                                             |
| ------------ | ------------------------------------------------- |
| Status       | Active                                            |
| Topic        | El evento que querés capturar (ej: Order created) |
| Delivery URL | `https://tu-dominio.com/ingest/tienda-principal`  |
| Secret       | El mismo secret que configuraste en el tenant     |

### Paso 4: Monitorear

Desde el Dashboard → **Monitor**:

- Verás cada webhook que llega en tiempo real
- Los webhooks pendientes muestran la **hora del próximo reintento**
- Click en 👁️ para ver el detalle completo:
  - Payload JSON formateado con botón **📋 Copiar**
  - **Historial de intentos** con código HTTP, error, duración y origen (Auto/Manual)
- Click en 🔄 para **reenviar manualmente** (genera un nuevo intento en el historial)
- Seleccioná múltiples con ☑ checkboxes y eliminá los que no necesites

---

## 📊 Dashboard (MudBlazor v8)

El Dashboard es una SPA Blazor Server con componentes **MudBlazor v8** (Material Design) y las siguientes secciones:

### 📊 Home (`/`)

Resumen general con cards MudPaper: tenants activos, webhooks en cola, entregados/fallidos hoy, actividad reciente con MudSimpleTable.

### 🏢 Tenants (`/tenants`)

CRUD completo de tenants con MudTextField, MudSelect, MudSwitch. Cada tenant define una tienda WooCommerce con su configuración de reenvío. Se puede asignar un grupo y una política de reintento individual o heredada del grupo.

### 📂 Grupos (`/grupos`)

Administración de grupos lógicos de tenants con MudSimpleTable y MudSelect. Cada grupo puede tener una política de reintento heredada por todos sus tenants.

### 🔄 Políticas de Reintento (`/politicas`)

Editor visual con MudNumericField y MudSelect para cada paso. Incluye **MudTimeline** con vista previa acumulada de los reintentos. Política "Estándar" incluida por defecto.

### 📡 Monitor (`/monitor`)

Vista en tiempo real con MudSimpleTable, MudCheckBox para selección múltiple, MudSelect para filtros por tenant y status. Muestra estado (MudChip), intentos, duración, fecha, y hora del próximo reintento. Acciones: ver detalle, reenviar, eliminar (individual y masivo).

### 📋 Detalle de Webhook (`/monitor/{id}`)

Vista completa de un webhook individual con cards de igual altura:
- **URL Origen** (sitio WooCommerce que generó el evento) y **URL Destino**
- Estado actual, número de intentos y fecha de recepción
- **MudTimeline** con historial de todos los intentos:
  - Número de intento secuencial
  - Código HTTP y duración
  - Descripción del error (si aplica)
  - Badge de origen: Auto OK / Manual
  - Fecha y hora
- **Payload JSON** formateado con botón **📋 Copiar** al portapapeles
- Acción de reenvío manual

### ⚙️ Configuración (`/configuracion`)

Credenciales de Cloudflare con MudTextField (con InputType.Password para API Token), MudButton y MudAlert para feedback.

---

## 🔗 API Endpoints

| Método | Ruta                                    | Descripción                             |
| ------ | --------------------------------------- | --------------------------------------- |
| `POST` | `/ingest/{slug}`                        | Recibe webhook de WooCommerce           |
| `PUT`  | `/api/tenants/{slug}/target-url`        | Actualiza la URL de destino de un tenant |
| `GET`  | `/api/tenants/{slug}`                   | Obtiene info de un tenant por slug       |
| `GET`  | `/health`                               | Health check                            |

### Ejemplo de ingesta

```bash
curl -X POST https://tu-dominio.com/ingest/mi-tienda \
  -H "Content-Type: application/json" \
  -H "X-WC-Webhook-Signature: <hmac-sha256-base64>" \
  -d '{"id": 123, "status": "processing"}'
```

### Actualizar URL destino de un tenant

Endpoint para que sistemas externos actualicen la URL de destino de un tenant sin usar el dashboard:

```bash
curl -X PUT https://tu-dominio.com/api/tenants/mi-tienda/target-url \
  -H "Content-Type: application/json" \
  -d '{"targetUrl": "https://nuevo-destino.com/api/webhooks"}'
```

**Respuesta exitosa (200):**
```json
{
  "slug": "mi-tienda",
  "name": "Mi Tienda",
  "targetUrl": "https://nuevo-destino.com/api/webhooks",
  "previousUrl": "https://viejo-destino.com/api/webhooks",
  "updatedAt": "2026-03-24T05:38:00Z"
}
```

**Validaciones:**
- URL debe ser `http://` o `https://` válida → `400 Bad Request`
- Slug no existe → `404 Not Found`
- El caché se invalida automáticamente tras la actualización

### Consultar info de un tenant

```bash
curl https://tu-dominio.com/api/tenants/mi-tienda
```

**Respuesta (200):**
```json
{
  "slug": "mi-tienda",
  "name": "Mi Tienda",
  "targetUrl": "https://mi-api.com/webhooks",
  "isActive": true,
  "createdAt": "2026-03-20T15:00:00Z",
  "updatedAt": "2026-03-24T05:38:00Z"
}
```

### Headers capturados en la ingesta

| Header                     | Descripción                                         |
| -------------------------- | --------------------------------------------------- |
| `X-WC-Webhook-Signature`   | Firma HMAC-SHA256 para validación de autenticidad    |
| `X-WC-Webhook-Source`      | URL del sitio WooCommerce origen (se registra como SourceUrl) |

### Headers enviados al destino

El Gateway reenvía el webhook al destino con los siguientes headers:

| Header                    | Descripción                        |
| ------------------------- | ---------------------------------- |
| `Content-Type`            | `application/json`                 |
| `X-WC-Webhook-Signature`  | Firma HMAC original de WooCommerce |
| `X-Gateway-Attempt`       | Número de intento actual           |
| `X-Gateway-DeliveryId`    | ID del delivery log                |

---

## 🔄 Políticas de Reintento

### Concepto

Cada política define una secuencia de **pasos**. Cuando un webhook falla, el sistema avanza al siguiente paso y programa el reintento:

```
Intento 1 → falla → esperar paso 1 (5s) → Intento 2 → falla → esperar paso 2 (30s) → ...
```

### Política "Estándar" (seed por defecto)

| Paso | Espera      | Acumulado    |
| ---- | ----------- | ------------ |
| 1    | 5 segundos  | 5s           |
| 2    | 30 segundos | 35s          |
| 3    | 2 minutos   | 2min 35s     |
| 4    | 15 minutos  | 17min 35s    |
| 5    | 1 hora      | 1h 17min 35s |

### Unidades disponibles

- **Segundos** — para reintentos rápidos
- **Minutos** — para errores temporales
- **Horas** — para destinos con downtime planificado
- **Días** — para reintentos de última oportunidad (ej: al otro día)

### Herencia de políticas

Las políticas se resuelven con el siguiente orden de prioridad:

```
1. Política asignada directamente al tenant (mayor prioridad)
2. Política asignada al grupo del tenant
3. Política por defecto del sistema
```

### Ciclo de vida del webhook

```
Nuevo → [Primer intento]
        ├─ Éxito ✅ → Success
        └─ Falla → Scheduled (NextRetryAt = now + paso[0])
                    ↓
              [Scheduler cada 5s]
              ├─ NextRetryAt <= now → Reintenta
              │   ├─ Éxito ✅ → Success
              │   └─ Falla → avanza paso, recalcula NextRetryAt
              │       └─ Sin más pasos → Failed ❌ (definitivo)
              └─ NextRetryAt > now → espera

Reenvío Manual → Genera un nuevo intento (sin afectar el contador de pasos)
              ├─ Éxito ✅ → Success
              └─ Falla → mantiene el estado actual
```

---

## 🌐 Túnel Cloudflare

El Gateway incluye integración automática con Cloudflare Tunnel para exponer el servicio con HTTPS sin configurar certificados ni abrir puertos.

### Configuración

Desde el Dashboard → **Configuración**:

1. Ingresá tu **API Token** de Cloudflare (con permisos de Tunnel y DNS)
2. Seleccioná la **Zona DNS** (tu dominio)
3. Definí el **subdominio** (ej: `gateway-wc.tu-dominio.com`)

El túnel se conecta automáticamente al iniciar la aplicación. El estado se muestra en la barra superior del dashboard:

- 🟢 **Túnel activo**: `https://gateway-wc.tu-dominio.com`
- 🔴 **Túnel desconectado**: con enlace para configurar

### Requisitos del API Token

El API Token de Cloudflare necesita los siguientes permisos:

| Permiso          | Tipo     | Recurso  |
| ---------------- | -------- | -------- |
| Cloudflare Tunnel | Edit    | Account  |
| DNS Records       | Edit    | Zone     |

---

## 📁 Estructura del Proyecto

```
dotar-gateway/
├── docker-compose.yml          # Orquestación Docker (app + Redis)
├── Dockerfile                  # Build multi-stage .NET 9
├── Dotar.Gateway.slnx          # Solución .NET
│
├── src/Dotar.Gateway/
│   ├── Dashboard/
│   │   └── Components/
│   │       ├── Layout/         # MainLayout (sidebar colapsable), NavMenu
│   │       └── Pages/
│   │           ├── Home.razor              # Dashboard principal
│   │           ├── Tenants.razor           # CRUD de tenants
│   │           ├── TenantGroups.razor       # Gestión de grupos
│   │           ├── RetryPolicies.razor      # Editor de políticas
│   │           ├── Monitor.razor           # Monitor de webhooks
│   │           ├── WebhookDetail.razor     # Detalle con historial
│   │           └── Configuracion.razor     # Config Cloudflare
│   │
│   ├── Domain/
│   │   ├── Entities/
│   │   │   ├── Tenant.cs              # Tienda WooCommerce
│   │   │   ├── TenantGroup.cs         # Agrupación lógica
│   │   │   ├── DeliveryLog.cs         # Registro maestro de entrega
│   │   │   ├── DeliveryAttempt.cs     # Registro individual por intento
│   │   │   ├── RetryPolicy.cs        # Política de reintento
│   │   │   ├── RetryStep.cs          # Paso dentro de una política
│   │   │   └── AppSetting.cs         # Configuraciones persistentes
│   │   └── Models/
│   │       ├── QueuedWebhook.cs       # Payload en cola Redis
│   │       └── ForwardResult.cs       # Resultado de reenvío
│   │
│   ├── Endpoints/
│   │   ├── IngestEndpoints.cs          # POST /ingest/{slug}
│   │   └── TenantApiEndpoints.cs       # PUT/GET /api/tenants/{slug}
│   │
│   ├── Infrastructure/
│   │   ├── Data/
│   │   │   └── GatewayDbContext.cs     # EF Core + SQLite
│   │   ├── Security/
│   │   │   └── HmacSignatureValidator.cs
│   │   ├── Services/
│   │   │   ├── RedisQueueService.cs
│   │   │   ├── ForwardingService.cs
│   │   │   ├── TenantCacheService.cs
│   │   │   └── MonitorNotificationService.cs
│   │   └── Tunnel/
│   │       ├── CloudflareTunnelManager.cs
│   │       └── TunnelStatusService.cs
│   │
│   ├── Workers/
│   │   ├── WebhookDispatcherWorker.cs  # Background worker
│   │   └── TunnelStartupService.cs
│   │
│   ├── Migrations/                     # EF Core (auto-apply)
│   ├── wwwroot/
│   │   └── css/app.css                 # Overrides mínimos (~50 líneas)
│   └── Program.cs                      # DI y configuración
│
└── tests/
    └── Dotar.Gateway.Tests/            # Tests unitarios
```

---

## 🗃️ Modelo de Datos

```
┌──────────────┐       ┌──────────────┐
│ TenantGroup  │       │ RetryPolicy  │
│──────────────│       │──────────────│
│ Id           │       │ Id           │
│ Name         │◄──────│ Name         │
│ RetryPolicyId│       │ IsDefault    │
│ CreatedAt    │       │ CreatedAt    │
└──────┬───────┘       └──────┬───────┘
       │ 1:N                  │ 1:N
       ▼                      ▼
┌──────────────┐       ┌──────────────┐
│   Tenant     │       │  RetryStep   │
│──────────────│       │──────────────│
│ Id           │       │ Id           │
│ Name         │       │ RetryPolicyId│
│ Slug         │       │ Order        │
│ DestinationUrl│      │ DelayValue   │
│ WebhookSecret│       │ DelayUnit    │
│ GroupId      │       └──────────────┘
│ RetryPolicyId│
│ IsActive     │
└──────┬───────┘
       │ 1:N
       ▼
┌──────────────┐
│ DeliveryLog  │
│──────────────│
│ Id           │
│ TenantId     │
│ Status       │
│ Payload      │
│ SourceUrl    │
│ TargetUrl    │
│ RetryCount   │
│ NextRetryAt  │
│ CreatedAt    │
└──────┬───────┘
       │ 1:N
       ▼
┌────────────────┐
│DeliveryAttempt │
│────────────────│
│ Id             │
│ DeliveryLogId  │
│ AttemptNumber  │
│ HttpStatusCode │
│ DurationMs     │
│ ErrorMessage   │
│ IsManual       │
│ CreatedAt      │
└────────────────┘
```

---

## 🛡️ Seguridad

- **HMAC-SHA256**: Cada webhook entrante se valida contra el secret del tenant
- **Aislamiento multi-tenant**: Cada tenant opera de forma independiente
- **Circuit Breaker**: Protección contra destinos caídos (evita cascada de errores)
- **Cloudflare Tunnel**: HTTPS sin exponer la máquina al internet
- **No hardcodea credenciales**: Todo se configura vía dashboard o variables de entorno
- **Persistencia encriptable**: SQLite soporta encryption-at-rest si se requiere

---

## 🔍 Troubleshooting

### El webhook no llega al destino

1. Verificá que el tenant esté **activo** (`IsActive = true`)
2. Verificá que la **URL destino** sea accesible desde el servidor del Gateway
3. Revisá el **Monitor** → detalle del webhook → historial de intentos para ver el error exacto
4. Comprobá que el **secret de WooCommerce** coincida con el del tenant

### Webhook rechazado (401 / firma inválida)

1. El secret en WooCommerce debe ser **idéntico** al configurado en el tenant
2. Verificá que WooCommerce esté enviando el header `X-WC-Webhook-Signature`

### Redis no conecta

```bash
# Verificar que Redis está corriendo
docker exec gateway-redis redis-cli ping
# Debe responder: PONG

# Ver logs del contenedor
docker-compose logs gateway-redis
```

### Base de datos SQLite

La base de datos se crea automáticamente en el primer inicio. Las migraciones se aplican automáticamente.

```bash
# Ubicación en Docker
docker exec gateway-app ls -la /app/data/

# Backup
docker cp gateway-app:/app/data/gateway.db ./backup-gateway.db
```

### Reconstruir desde cero

```bash
docker-compose down -v    # ⚠️ Elimina volúmenes (datos)
docker-compose up -d --build
```

---

## 📝 Licencia

MIT License — Ver [LICENSE](LICENSE) para más detalles.

---

<div align="center">

Desarrollado con ❤️ por [Dotar Soluciones](https://dotarsoluciones.com)

</div>
