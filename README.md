# 🚀 Webhooks Gateway

<div align="center">

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet)
![MudBlazor](https://img.shields.io/badge/MudBlazor-v8-7B1FA2?style=for-the-badge)
![Blazor Server](https://img.shields.io/badge/Blazor-Server-512BD4?style=for-the-badge&logo=blazor)
![SQLite](https://img.shields.io/badge/SQLite-003B57?style=for-the-badge&logo=sqlite)
![Redis](https://img.shields.io/badge/Redis-DC382D?style=for-the-badge&logo=redis)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker)
![Cloudflare](https://img.shields.io/badge/Cloudflare-F38020?style=for-the-badge&logo=cloudflare)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**Gateway intermediario multi-tenant, agnóstico a la plataforma origen, que recibe, valida con HMAC, encola, reenvía y reintenta webhooks con políticas configurables, monitoreo en tiempo real y túnel HTTPS automático vía Cloudflare.**

[Funcionalidad](#-funcionalidad) · [Arquitectura](#-arquitectura) · [Despliegue](#-despliegue) · [Configuración](#-configuración) · [API](#-api-reference) · [Operación](#-operación)

</div>

---

## 📋 Tabla de Contenidos

- [TL;DR](#-tldr)
- [¿Qué problema resuelve?](#-qué-problema-resuelve)
- [Funcionalidad](#-funcionalidad)
  - [Pipeline de un webhook](#pipeline-de-un-webhook)
  - [Esquemas HMAC soportados](#esquemas-hmac-soportados)
  - [Propagación de headers](#-propagación-de-headers)
  - [Políticas de reintento](#políticas-de-reintento)
  - [Multi-tenant y grupos](#multi-tenant-y-grupos)
- [Arquitectura](#-arquitectura)
- [Despliegue](#-despliegue)
  - [Docker Compose (recomendado)](#1-docker-compose-recomendado)
  - [Ejecución manual](#2-ejecución-manual-desarrollo)
  - [Producción](#3-consideraciones-de-producción)
- [Configuración](#-configuración)
  - [Variables y appsettings](#variables-y-appsettings)
  - [Túnel Cloudflare](#túnel-cloudflare)
  - [API Key del Gateway](#api-key-del-gateway)
  - [Crear tenants](#crear-tenants)
- [API Reference](#-api-reference)
- [Operación](#-operación)
- [Modelo de datos](#-modelo-de-datos)
- [Seguridad](#-seguridad)
- [Troubleshooting](#-troubleshooting)

---

## ⚡ TL;DR

```bash
git clone https://github.com/alejandrop1105/webhooks-gateway.git
cd webhooks-gateway
docker-compose up -d --build

# 1. Abrir el dashboard
open http://localhost:8082

# 2. Copiar la API Key generada (Configuración → API Key del Gateway)
#    o leerla del log: docker-compose logs gateway-app | grep "API Key"

# 3. Crear un tenant desde un sistema externo
curl -X POST http://localhost:8082/api/tenants \
  -H "X-Gateway-Api-Key: <tu-api-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Mi Sistema",
    "slug": "mi-sistema",
    "targetUrl": "https://mi-api.com/webhooks",
    "signatureScheme": "GitHub"
  }'
# Devuelve 201 con el webhookSecret generado — guardalo, no se puede recuperar.

# 4. Configurar el origen para emitir webhooks a:
#    POST http://localhost:8082/ingest/mi-sistema
#    con HMAC-SHA256 del body en el header del esquema elegido.
```

---

## 🎯 ¿Qué problema resuelve?

Cuando una plataforma emite webhooks (WooCommerce, GitHub, Stripe, Shopify, Mercado Pago, sistema propio, …) hacia tu sistema interno, te chocás con problemas comunes:

| Problema                                                              | Lo que el Gateway hace                                          |
| --------------------------------------------------------------------- | --------------------------------------------------------------- |
| Reintentos limitados o inexistentes en el origen                      | Política configurable por pasos (segundos → días)                |
| Si tu API se cae, el webhook se pierde                                | Cola Redis + scheduler que reintenta hasta agotar la política    |
| No hay visibilidad de qué llegó y qué falló                           | Dashboard con historial completo por webhook                     |
| Cada plataforma tiene su esquema HMAC distinto                        | Esquemas configurables por tenant (WooCommerce/GitHub/Generic)   |
| Los headers del provider se pierden y rompen el procesamiento         | Propagación verbatim de todos los `X-*` (topic, signature, delivery-id, …) |
| Tu sistema interno no debería estar expuesto a internet               | Cloudflare Tunnel: HTTPS automático, sin abrir puertos           |
| Múltiples sistemas origen requieren múltiples integraciones           | Un solo Gateway, N tenants agrupables, configuración unificada   |
| Los destinos cambian (migración, blue/green) y hay que reconfigurar   | API REST para actualizar URL destino sin tocar el origen         |
| Los timeouts/SSL del origen son frágiles                              | Circuit breaker con Polly + manejo robusto de errores HTTP       |

El Gateway funciona como **buffer y traductor entre productores y consumidores de webhooks**: el origen entrega rápido y se desentiende, el Gateway absorbe el pico, persiste, valida, reintenta, observa y entrega.

---

## ✨ Funcionalidad

### Pipeline de un webhook

```
┌────────────┐                                                        ┌──────────────┐
│  Origen    │ ① POST /ingest/{slug}                                  │   Destino    │
│  (cualq.)  │ ──────────────────►  GATEWAY                           │   final      │
└────────────┘                                                        └──────────────┘
                                                                              ▲
                ② lookup tenant (caché en memoria)                            │
                ③ validar HMAC según SignatureScheme                          │
                ④ encolar en Redis (200 OK al origen)                         │ ⑦
                ⑤ filtrar y persistir headers X-* del provider               │ POST + headers
                ⑥ worker dequeue (background)                                 │ X-* verbatim
                ⑦ POST al TargetUrl con headers + Polly                       │
                ⑦ si falla:                                                   │
                   ├─ persistir DeliveryLog (status=Scheduled)                │
                   ├─ calcular NextRetryAt = now + paso[N]                    │
                   └─ scheduler vuelve a intentar                             │
                ⑧ si éxito: DeliveryLog status=Success
                ⑨ historial de cada intento → DeliveryAttempt
```

**Detalle de cada paso:**

1. **Recepción** ([IngestEndpoints.cs](src/Dotar.Gateway/Endpoints/IngestEndpoints.cs)). Endpoint minimal API. Lee el body como bytes para preservar la firma exacta.
2. **Lookup tenant**. Resuelve el `slug` contra una caché en memoria con sliding expiration (default 5 minutos, configurable). Si el tenant está inactivo, responde `401` sin filtrarse información.
3. **Validación HMAC** ([HmacSignatureValidator.cs](src/Dotar.Gateway/Infrastructure/Services/HmacSignatureValidator.cs)). Calcula `HMAC-SHA256(secret, body)` y compara contra el header esperado en formato timing-safe (`CryptographicOperations.FixedTimeEquals`) según el esquema del tenant.
4. **Filtrado y captura de headers** ([HeaderForwardingPolicy.cs](src/Dotar.Gateway/Infrastructure/Services/HeaderForwardingPolicy.cs)). Se conservan los headers del provider (`X-WC-Webhook-Topic`, `X-Hub-Signature-256`, `X-Signature` de Mercado Pago, etc.) y se descartan los de transporte/proxy (`Host`, `Content-Length`, `X-Forwarded-*`, `Cf-*`, `X-Real-IP`). Detalle en [Propagación de headers](#-propagación-de-headers).
5. **Encolar**. Push a Redis con el payload, slug, target, sourceUrl y los headers filtrados. Devuelve `202 Accepted` al origen. **El origen ya no espera al destino final.**
6. **Worker dispatcher** ([WebhookDispatcherWorker.cs](src/Dotar.Gateway/Workers/WebhookDispatcherWorker.cs)). Background service que hace `BLPOP` sobre Redis y procesa cada item.
7. **Forward** ([ForwardingService.cs](src/Dotar.Gateway/Infrastructure/Services/ForwardingService.cs)). POST al `TargetUrl` con `HttpClient` + Polly (circuit breaker, timeout). Aplica los headers del provider con `TryAddWithoutValidation` para preservar nombre y valor exactos. Suma `X-Dotar-Gateway-ID` con el slug del tenant.
8. **Reintentos**. Si falla, persiste un `DeliveryLog` con `Status = Scheduled` y `NextRetryAt` calculado según la política aplicable. Los headers van también persistidos (`ForwardedHeadersJson`) para que retries y reenvíos manuales —que leen desde DB, no desde Redis— los propaguen igual.
9. **Persistencia exitosa**. Cada intento queda registrado como `DeliveryAttempt` (HTTP code, duración ms, error, manual o automático).
10. **Observabilidad**. El dashboard refleja en tiempo real cada cambio mediante `MonitorNotificationService` (eventos in-process).

### Esquemas HMAC soportados

Cada tenant configura **qué esquema usa su sistema origen** para firmar el body. El Gateway calcula el HMAC-SHA256 del body con el secret del tenant y compara contra el header esperado según el esquema.

| Esquema       | Header default              | Formato esperado            | Plataformas típicas                                     |
| ------------- | --------------------------- | --------------------------- | ------------------------------------------------------- |
| `WooCommerce` | `X-WC-Webhook-Signature`    | Base64 del HMAC-SHA256      | WooCommerce, plugins compatibles                        |
| `GitHub`      | `X-Hub-Signature-256`       | `sha256=<hex-lowercase>`    | GitHub, GitLab, Gitea, Bitbucket Server                 |
| `Generic`     | configurable por tenant     | Hex lowercase del HMAC-SHA256 | Sistemas propios, Mercado Pago, Stripe (con adaptación) |
| `None`        | —                           | —                           | Tenants sin firma (sólo entornos confiables)            |

**Override de header por tenant**: si tu origen usa el esquema `Generic` o necesitás que un esquema estándar lea de un header no estándar, podés sobrescribir `SignatureHeader` por tenant.

**Generación de firma de referencia (Node.js):**

```javascript
const crypto = require('crypto');
const secret = 'tu-webhook-secret';
const body = JSON.stringify(payload);
const hmac = crypto.createHmac('sha256', secret).update(body, 'utf8');

// WooCommerce
const wcSig = hmac.copy().digest('base64');
// GitHub
const ghSig = 'sha256=' + hmac.copy().digest('hex');
// Generic
const genSig = hmac.copy().digest('hex');
```

### 🔁 Propagación de headers

El Gateway reenvía verbatim al downstream **todos los headers del provider** que arrancan con `X-`, preservando nombre y valor exactos. Sin esto, el receptor pierde contexto crítico (tipo de evento, firma HMAC, ID de delivery para deduplicar).

**Política implementada en [HeaderForwardingPolicy.cs](src/Dotar.Gateway/Infrastructure/Services/HeaderForwardingPolicy.cs):**

| Categoría                          | Acción                  | Ejemplos                                                                  |
| ---------------------------------- | ----------------------- | ------------------------------------------------------------------------- |
| Headers `X-*` del provider         | ✅ Reenviar verbatim     | `X-WC-Webhook-Topic`, `X-Hub-Signature-256`, `X-Signature`, `X-Request-Id` |
| `User-Agent` original              | ✅ Reenviar como `X-Original-User-Agent` | `WooCommerce/8.5; Verifying`                          |
| `X-Forwarded-*`, `X-Real-IP`       | ❌ No reenviar (proxy)   | `X-Forwarded-For`, `X-Forwarded-Host`                                     |
| `Cf-*`, `Cdn-Loop`                 | ❌ No reenviar (CDN)     | `Cf-Ray`, `Cf-Connecting-IP`                                              |
| Hop-by-hop / transporte            | ❌ No reenviar           | `Host`, `Connection`, `Content-Length`, `Transfer-Encoding`, `Keep-Alive` |
| Pseudo-headers HTTP/2 (`:`)        | ❌ No reenviar           | `:authority`, `:method`                                                   |

**Por qué importa cada header en el caso WooCommerce:**

| Header                         | Para qué lo usa el downstream                                                            |
| ------------------------------ | ---------------------------------------------------------------------------------------- |
| `X-WC-Webhook-Topic`           | Decidir acción: `order.created` → crear, `order.updated` → mergear, `order.deleted` → borrar |
| `X-WC-Webhook-Event`           | Fallback / desambiguación cuando Topic no alcanza (`created`/`updated`/`deleted`/`restored`) |
| `X-WC-Webhook-Resource`        | Tipo de recurso (`order`, `product`, `customer`, `coupon`)                                |
| `X-WC-Webhook-Signature`       | Validar HMAC del body con el secret compartido (anti-spoofing)                            |
| `X-WC-Webhook-Delivery-ID`     | Idempotencia: WooCommerce reintenta hasta 5 veces; sin este ID se procesa N veces lo mismo |
| `X-WC-Webhook-Source`          | Trazabilidad: qué tienda originó el evento                                                |

**Mismo criterio para otros providers:**

| Provider     | Headers que se propagan automáticamente                                |
| ------------ | ---------------------------------------------------------------------- |
| Mercado Pago | `X-Signature`, `X-Request-Id`                                          |
| VTEX / INNEW | `X-Webhook-Secret`, `X-Api-Key`                                        |
| GitHub       | `X-Hub-Signature-256`, `X-GitHub-Event`, `X-GitHub-Delivery`           |
| PedidosYa    | Cualquier `X-*` específico que envíe el provider                       |
| Custom       | Cualquier `X-*` (incluyendo headers propios del cliente)               |

**Garantías:**

- **Capitalización exacta preservada** vía `HttpRequestMessage.Headers.TryAddWithoutValidation`. Algunos validadores estrictos rechazan `Http-X-Wc-Webhook-Topic` o `X_WC_Webhook_Topic`; el Gateway nunca renombra.
- **Multi-valor** unificado por coma según RFC 7230 §3.2.2.
- **Persistencia para retries**: los headers se guardan en `DeliveryLog.ForwardedHeadersJson`. Reintentos automáticos y manuales (que leen de DB, no de Redis) los reenvían igual.
- **Defensa en profundidad**: `ForwardingService` re-aplica la policy aunque la cola venga con headers fabricados, así no se pueden inyectar `Host` o `X-Forwarded-*` desde el origen.

**Test de aceptación**: [`IngestHeaderPropagationTests.cs`](tests/Dotar.Gateway.Tests/IngestHeaderPropagationTests.cs) ejercita un POST a `/ingest/{slug}` con los 6 headers WooCommerce + ruido de proxy/CDN, y verifica que cada header del provider llega al outgoing con mismo nombre y valor, y que los de proxy quedan afuera.

### Políticas de reintento

Cada **política** es una secuencia ordenada de **pasos**. Cuando un webhook falla, avanza al siguiente paso y agenda el reintento en `NextRetryAt`.

```
fallo → esperar paso[0] → reintento → fallo → paso[1] → ... → paso[N] sin más → Failed (definitivo)
```

**Política `Estándar` incluida por defecto:**

| Paso | Espera      | Acumulado    |
| ---- | ----------- | ------------ |
| 1    | 5 segundos  | 5s           |
| 2    | 30 segundos | 35s          |
| 3    | 2 minutos   | 2min 35s     |
| 4    | 15 minutos  | 17min 35s    |
| 5    | 1 hora      | 1h 17min 35s |

**Resolución de política aplicable** (de mayor a menor prioridad):

```
1. Política asignada directamente al tenant
2. Política asignada al grupo del tenant
3. Política con IsDefault = true
```

**Unidades**: Segundos, Minutos, Horas, Días. Se editan visualmente desde [/politicas](src/Dotar.Gateway/Dashboard/Components/Pages/RetryPolicies.razor) con vista previa de timeline acumulado.

**Reenvío manual**: desde el monitor podés disparar un intento out-of-band. No afecta al contador de pasos automáticos; queda registrado como `DeliveryAttempt.IsManual = true`.

**Circuit breaker** (Polly): si un destino acumula N fallos consecutivos en una ventana, las requests siguientes se cortan en seco durante un período sin pegarle al destino — protege al destino caído de cascadas.

### Multi-tenant y grupos

Un Gateway aloja N **tenants** (sistemas origen), opcionalmente organizados en **grupos** (clientes, regiones, ambientes, etc.).

```
Grupo "Cliente ABC"  ─┬─ Tenant "abc-prod"   → https://api.abc.com/webhooks
                      ├─ Tenant "abc-stage"  → https://stage.abc.com/webhooks
                      └─ Tenant "abc-bi"     → https://bi.abc.com/ingest

Grupo "Cliente XYZ"  ─── Tenant "xyz-orders" → https://xyz.io/orders/webhook
```

Cada tenant tiene su propio: slug (URL pública `/ingest/{slug}`), secret HMAC, esquema, target URL, política de reintento (heredable del grupo), estado activo/inactivo.

---

## 🏗️ Arquitectura

```
                                ┌──────────────────────────────────────────────────┐
                                │                 WEBHOOKS GATEWAY                  │
┌──────────────┐                │                                                  │
│   Origen A   │ HTTPS          │  ┌─────────────────┐    ┌────────────────────┐   │
│ (WooCommerce)│ ──────────────►│──│ IngestEndpoint  │───►│   Redis Queue      │   │
└──────────────┘                │  │ HMAC validate   │    │   (LIST BLPOP)     │   │
                                │  │ Tenant lookup   │    └─────────┬──────────┘   │
┌──────────────┐                │  │ (cache 5 min)   │              │              │
│   Origen B   │ HTTPS          │  └─────────────────┘              ▼              │
│   (GitHub)   │ ──────────────►│           ▲              ┌────────────────────┐  │   API Destino A
└──────────────┘                │           │              │  Dispatcher Worker │──┼─►
                                │           │              │  (HttpClient+Polly)│  │   API Destino B
┌──────────────┐                │  ┌────────┴────────┐    │  Forward + Retry   │──┼─►
│   Origen C   │ HTTPS          │  │  /api/tenants   │    └─────────┬──────────┘  │   API Destino C
│  (Custom)    │ ──────────────►│──│  CRUD via API   │              │             │──►
└──────────────┘                │  │  X-Gateway-Api  │              ▼
                                │  └─────────────────┘    ┌────────────────────┐
                                │                          │  SQLite + WAL      │
                                │  ┌─────────────────┐    │  Tenants, Groups   │
                                │  │ Blazor Dashboard│◄──►│  RetryPolicies     │
                                │  │ Tema dark, Mud  │    │  DeliveryLog       │
                                │  │ Real-time mon.  │    │  DeliveryAttempt   │
                                │  └─────────────────┘    │  AppSettings       │
                                │                          └────────────────────┘
                                │  ┌─────────────────┐
                                │  │CloudflareTunnel │
                                │  │(start automático)│
                                │  └────────┬────────┘
                                └───────────┼──────────────────────────────────────┘
                                            │ HTTPS automático
                                            ▼
                                   webhooks-gateway.tu-dominio.com
```

**Decisiones clave:**

- **SQLite + WAL** en lugar de Postgres/SQL Server: cero administración, ideal para deploys self-hosted, soporta escrituras concurrentes con WAL, embebido en el contenedor.
- **Redis** como cola: latencia de microsegundos, BLPOP atómico, soporta múltiples workers escalables horizontalmente.
- **Caché en memoria** del tenant: cada `/ingest` resuelve el tenant sin pegarle a SQLite. Invalidación explícita al editar.
- **Blazor Server** para el dashboard: render server-side, websocket persistente, eventos en tiempo real sin JS extra.
- **Cloudflare Tunnel**: HTTPS automático, sin certificados, sin abrir puertos en el firewall, ideal para máquinas detrás de NAT.

---

## 🚀 Despliegue

### 1. Docker Compose (recomendado)

Para desarrollo y producción small-scale. El stack levanta dos servicios: la app y Redis con persistencia AOF.

#### Requisitos

| Componente     | Versión |
| -------------- | ------- |
| Docker         | 20.10+  |
| Docker Compose | 2.0+    |

#### Arrancar

```bash
git clone https://github.com/alejandrop1105/webhooks-gateway.git
cd webhooks-gateway
docker-compose up -d --build
```

| Servicio        | Puerto externo | Puerto interno | Descripción                          |
| --------------- | -------------- | -------------- | ------------------------------------ |
| `gateway-app`   | `8082`         | `5200`         | App + dashboard                      |
| `gateway-redis` | `6380`         | `6379`         | Cola de webhooks (persistencia AOF)  |

| Volumen              | Path interno         | Descripción                    |
| -------------------- | -------------------- | ------------------------------ |
| `gateway-app-data`   | `/app/data`          | SQLite + db de Cloudflare creds |
| `gateway-redis-data` | `/data` (en redis)   | Datos de Redis (AOF)            |

> ⚠️ **Persistencia de Data Protection Keys**: por default ASP.NET escribe las keys del antiforgery en `/home/app/.aspnet/DataProtection-Keys` (dentro del contenedor). Al recrear el contenedor, los usuarios logueados en el dashboard tendrán que refrescar. Si te molesta, mapeá un volumen extra a esa ruta en `docker-compose.yml`.

#### Comandos comunes

```bash
docker-compose up -d --build         # Build + start
docker-compose down                  # Stop (preserva volúmenes)
docker-compose down -v               # Stop + DROP DATA (¡cuidado!)
docker-compose logs -f gateway-app   # Logs en vivo
docker-compose ps                    # Estado de los servicios
docker-compose restart gateway-app   # Reiniciar sólo la app
```

### 2. Ejecución manual (desarrollo)

Útil para iterar rápido sobre el código.

#### Requisitos

| Componente         | Versión               |
| ------------------ | --------------------- |
| .NET SDK           | 9.0+                  |
| Redis              | 6.0+ (local o Docker) |
| Cloudflare account | Opcional              |

#### Comandos

```bash
# Levantar Redis (Docker es lo más rápido)
docker run -d --name redis -p 6379:6379 redis:alpine

# Restaurar dependencias y correr
dotnet restore src/Dotar.Gateway/Dotar.Gateway.csproj
dotnet run --project src/Dotar.Gateway/Dotar.Gateway.csproj
# → http://localhost:5200

# Tests
dotnet test
```

#### Hot reload

```bash
dotnet watch --project src/Dotar.Gateway/Dotar.Gateway.csproj
```

### 3. Consideraciones de producción

- **HTTPS**: usá Cloudflare Tunnel (incluido) o ponete detrás de un reverse proxy (nginx, Traefik, Caddy). El Gateway escucha plain HTTP por dentro.
- **Backup de SQLite**: la DB vive en `/app/data/gateway.db` dentro del contenedor. Backup vía:
  ```bash
  docker exec gateway-app sqlite3 /app/data/gateway.db ".backup /app/data/backup-$(date +%F).db"
  docker cp gateway-app:/app/data/backup-2026-04-28.db ./
  ```
- **Backup de Redis (cola pendiente)**: el AOF en `gateway-redis-data` ya persiste. Si querés snapshot:
  ```bash
  docker exec gateway-redis redis-cli BGSAVE
  ```
- **Escalado horizontal**: la app es stateless excepto por el caché en memoria del tenant. Podés correr múltiples instancias detrás de un LB; cada una hará BLPOP a Redis y dispatcheará en paralelo. La invalidación de caché de tenants en N instancias requiere coordinar (TODO si lo necesitás).
- **Recursos típicos**: 1 vCPU + 256 MB RAM bastan para ~500 webhooks/min con destinos de latencia moderada.
- **Logs**: la app loggea a stdout, capturable por `docker logs` o tu runtime.
- **Zona horaria**: setear `TZ` en el contenedor para que los timestamps del dashboard sean locales (`TZ=America/Argentina/Buenos_Aires`).
- **Migraciones**: se aplican automáticamente al arrancar (`db.Database.MigrateAsync()`). El primer deploy puede tardar unos segundos extra en crear el schema.

---

## ⚙️ Configuración

### Variables y appsettings

Configuración en orden de precedencia: **variables de entorno** → `appsettings.{Environment}.json` → `appsettings.json`.

#### `appsettings.json` completo

```json
{
  "ConnectionStrings": {
    "Sqlite": "Data Source=gateway.db",
    "Redis": "localhost:6380"
  },
  "Gateway": {
    "TenantCacheMinutes": 5,
    "QueueKey": "gateway:webhooks",
    "RetryCount": 3,
    "CircuitBreakerThreshold": 5,
    "CircuitBreakerDurationSeconds": 30
  },
  "Cloudflare": {
    "TunnelName": "webhooks-gateway",
    "Domain": "tu-dominio.com",
    "ApiToken": "",
    "AccountId": "",
    "ZoneId": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

#### Referencia

| Sección             | Clave                            | Default                  | Descripción                                                              |
| ------------------- | -------------------------------- | ------------------------ | ------------------------------------------------------------------------ |
| `ConnectionStrings` | `Sqlite`                         | `Data Source=gateway.db` | Connection string a SQLite. En Docker: `Data Source=/app/data/gateway.db` |
| `ConnectionStrings` | `Redis`                          | `localhost:6380`         | Endpoint Redis. En Docker compose: `gateway-redis:6379`                  |
| `Gateway`           | `TenantCacheMinutes`             | `5`                      | TTL del caché en memoria de tenants                                       |
| `Gateway`           | `QueueKey`                       | `gateway:webhooks`       | Key Redis donde se encolan webhooks                                       |
| `Gateway`           | `CircuitBreakerThreshold`        | `5`                      | Fallos consecutivos antes de abrir el circuito                            |
| `Gateway`           | `CircuitBreakerDurationSeconds`  | `30`                     | Tiempo que el circuito permanece abierto                                  |
| `Cloudflare`        | `TunnelName`                     | `webhooks-gateway`       | Nombre del túnel/subdominio                                               |
| `Cloudflare`        | `Domain`                         | (vacío)                  | Dominio base gestionado en Cloudflare                                     |
| `Cloudflare`        | `ApiToken`                       | (vacío)                  | Token con permisos Tunnel:Edit + DNS:Edit                                 |
| `Cloudflare`        | `AccountId`                      | (vacío)                  | Account ID                                                                |
| `Cloudflare`        | `ZoneId`                         | (vacío)                  | Zone ID del dominio                                                       |

> **Configuración persistente desde el dashboard**: las credenciales de Cloudflare y la API Key del Gateway se guardan en la tabla `AppSettings` (SQLite). Una vez seteadas desde el dashboard, sobreviven a reinicios y son las que usa la app. Las del `appsettings.json` actúan como bootstrap si la DB está vacía.

#### Variables de entorno equivalentes (Docker / Kubernetes)

Cualquier setting se puede sobrescribir con env vars usando `__` como separador:

```bash
ConnectionStrings__Sqlite=Data Source=/data/gateway.db
ConnectionStrings__Redis=gateway-redis:6379
Gateway__TenantCacheMinutes=10
Cloudflare__TunnelName=mi-gateway
ASPNETCORE_ENVIRONMENT=Production
TZ=America/Argentina/Buenos_Aires
```

### Túnel Cloudflare

El Gateway provisiona el túnel automáticamente al arrancar si encuentra credenciales válidas. Configuración una sola vez:

1. **Crear API Token en Cloudflare**: Dashboard → My Profile → API Tokens → Create Token → Custom token con:

   | Permiso             | Tipo  | Recurso                          |
   | ------------------- | ----- | -------------------------------- |
   | Cloudflare Tunnel   | Edit  | Account: `<tu-cuenta>`           |
   | DNS                 | Edit  | Zone: `<tu-dominio>`             |

2. **Obtener Account ID y Zone ID**: Cloudflare Dashboard → tu dominio → Overview → sidebar derecho.

3. **Cargar credenciales**: Dashboard → **Configuración** → completar API Token, Account ID, Zone ID, nombre del túnel y dominio → **Guardar**.

4. **Iniciar túnel**: Click en **Crear / Reconectar Túnel**. Una vez activo, en la AppBar superior aparece el chip verde con la URL pública (`https://<TunnelName>.<Domain>`).

El túnel se reconecta automáticamente en cada arranque siempre que las credenciales estén persistidas.

### API Key del Gateway

Los endpoints `/api/tenants/*` están protegidos por una **API Key estática global** (header `X-Gateway-Api-Key`).

- **Generación automática**: en el primer arranque, si no hay key configurada, el Gateway genera una de 32 bytes hex y la persiste en `AppSettings`. Queda en el log con nivel `Warning` una sola vez. Buscala con:
  ```bash
  docker-compose logs gateway-app | grep "API Key"
  ```
- **Visualización**: dashboard → **Configuración** → **API Key del Gateway** (con botón Mostrar/Copiar).
- **Rotación**: dashboard → **Regenerar**. La nueva key invalida la anterior inmediatamente. Acordate de actualizar todos los sistemas externos que la usen.
- **Comparación timing-safe**: la validación usa `CryptographicOperations.FixedTimeEquals` para evitar timing attacks.

> 🔐 La API Key viaja como secret estático. Usá HTTPS siempre, no la commitees, rotala si sospechás compromiso. Para escenarios más estrictos (mTLS, OAuth client-credentials) considerá una capa adicional.

### Crear tenants

Dos caminos:

**A. Desde el dashboard** ([/tenants/create](src/Dotar.Gateway/Dashboard/Components/Pages/Tenants.razor)): UI completa con `MudSelect` para esquema HMAC y campo opcional para header custom.

**B. Vía API** (recomendado para automatización):

```bash
curl -X POST http://localhost:8082/api/tenants \
  -H "X-Gateway-Api-Key: <api-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Mi Tienda",
    "slug": "mi-tienda",
    "targetUrl": "https://mi-api.com/webhooks",
    "signatureScheme": "WooCommerce",
    "isActive": true
  }'
```

Si no enviás `webhookSecret`, el Gateway genera uno de 32 bytes hex y **lo devuelve en la respuesta de creación**. **Es la única vez que se devuelve**: guardalo donde corresponda.

---

## 📡 API Reference

| Método | Ruta                                  | Auth        | Status codes              |
| ------ | ------------------------------------- | ----------- | ------------------------- |
| `POST` | `/ingest/{slug}`                      | HMAC tenant | `202` `401`               |
| `POST` | `/api/tenants`                        | API Key     | `201` `400` `401` `409`   |
| `GET`  | `/api/tenants/{slug}`                 | API Key     | `200` `401` `404`         |
| `PUT`  | `/api/tenants/{slug}/target-url`      | API Key     | `200` `400` `401` `404`   |
| `GET`  | `/health`                             | —           | `200`                     |

### `POST /ingest/{slug}` — Ingesta de webhook

Endpoint público que reciben los sistemas origen.

**Headers requeridos** (según esquema del tenant):

| Esquema       | Header                       | Formato                   |
| ------------- | ---------------------------- | ------------------------- |
| `WooCommerce` | `X-WC-Webhook-Signature`     | base64                    |
| `GitHub`      | `X-Hub-Signature-256`        | `sha256=<hex>`            |
| `Generic`     | configurable o `X-Webhook-Signature` | hex lowercase     |

**Body**: cualquier payload que el origen envíe (típicamente JSON, pero se trata como bytes opacos para el HMAC).

**Headers que se propagan al downstream** (ver [Propagación de headers](#-propagación-de-headers)):
- Todos los `X-*` del provider con nombre y valor exactos.
- `User-Agent` original como `X-Original-User-Agent`.
- `X-Dotar-Gateway-ID: <slug-del-tenant>` agregado por el Gateway.

**Respuestas**:
- `202 Accepted` — encolado para reenvío.
- `401 Unauthorized` — tenant no existe / inactivo / firma inválida / body vacío. **No revela cuál de los casos** (anti-enumeration).

### `POST /api/tenants` — Crear tenant

```json
{
  "name": "Mi Tenant",                // requerido
  "slug": "mi-tenant",                 // requerido, ^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$|^[a-z0-9]$
  "targetUrl": "https://api.com/hook", // requerido, http o https
  "webhookSecret": null,                // opcional; si null, se genera y devuelve
  "signatureScheme": "WooCommerce",     // opcional; default WooCommerce
  "signatureHeader": null,              // opcional; override del header (típicamente para Generic)
  "isActive": true,                     // opcional; default true
  "retryPolicyId": null,                // opcional; FK a RetryPolicies
  "tenantGroupId": null                 // opcional; FK a TenantGroups
}
```

**Respuesta `201 Created`**:

```json
{
  "slug": "mi-tenant",
  "name": "Mi Tenant",
  "targetUrl": "https://api.com/hook",
  "webhookSecret": "f3e2d1c0...",
  "signatureScheme": "WooCommerce",
  "signatureHeader": null,
  "isActive": true,
  "retryPolicyId": null,
  "tenantGroupId": null,
  "createdAt": "2026-04-28T10:15:00Z"
}
```

Header `Location: /api/tenants/mi-tenant`.

**Errores**:
- `400` — name/slug/targetUrl faltantes; slug inválido; URL no http/https; retryPolicyId/tenantGroupId inexistente.
- `401` — sin / con API Key inválida.
- `409` — slug ya en uso.

### `GET /api/tenants/{slug}` — Obtener tenant

```bash
curl http://localhost:8082/api/tenants/mi-tenant -H "X-Gateway-Api-Key: <key>"
```

```json
{
  "slug": "mi-tenant",
  "name": "Mi Tenant",
  "targetUrl": "https://api.com/hook",
  "signatureScheme": "WooCommerce",
  "signatureHeader": null,
  "isActive": true,
  "createdAt": "2026-04-28T10:15:00Z",
  "updatedAt": "2026-04-28T10:30:00Z"
}
```

> El `webhookSecret` no se devuelve en `GET`. Si se perdió, hay que regenerar el tenant o editarlo desde el dashboard.

### `PUT /api/tenants/{slug}/target-url` — Actualizar URL destino

Útil para migraciones blue/green o cambios de infraestructura sin reconfigurar el origen.

```bash
curl -X PUT http://localhost:8082/api/tenants/mi-tenant/target-url \
  -H "X-Gateway-Api-Key: <key>" \
  -H "Content-Type: application/json" \
  -d '{"targetUrl": "https://nuevo-destino.com/hook"}'
```

```json
{
  "slug": "mi-tenant",
  "name": "Mi Tenant",
  "targetUrl": "https://nuevo-destino.com/hook",
  "previousUrl": "https://api.com/hook",
  "updatedAt": "2026-04-28T11:00:00Z"
}
```

El caché del tenant se invalida automáticamente — el cambio aplica al próximo webhook entrante.

### `GET /health`

Health check sin auth. Devuelve `200` con `{ status, timestamp }`. Pensado para load balancers y supervisores.

---

## 🛠️ Operación

### Dashboard (`http://localhost:8082`)

Construido con **MudBlazor v8** y tema dark. Secciones:

| Página             | Ruta              | Función principal                                              |
| ------------------ | ----------------- | -------------------------------------------------------------- |
| Home               | `/`               | KPIs (tenants activos, en cola, entregados/fallidos hoy) + actividad reciente |
| Tenants            | `/tenants`        | CRUD de tenants con esquema HMAC y header configurables       |
| Grupos             | `/grupos`         | Agrupar tenants y asignar política heredada                    |
| Políticas          | `/politicas`      | Editor visual con timeline de pasos                            |
| Monitor            | `/monitor`        | Lista de webhooks con filtros, acciones masivas, refresh       |
| Detalle de webhook | `/monitor/{id}`   | Payload + historial completo de intentos + reenvío manual      |
| Configuración      | `/configuracion`  | Cloudflare creds + API Key del Gateway                         |

**Indicador de túnel** en la AppBar superior: chip verde con la URL pública si está activo, rojo con link a Configuración si no.

### Backup y restore

```bash
# Backup atómico de SQLite (la DB sigue corriendo)
docker exec gateway-app sqlite3 /app/data/gateway.db ".backup /tmp/backup.db"
docker cp gateway-app:/tmp/backup.db ./backup-$(date +%F).db

# Restore (con la app DETENIDA)
docker-compose stop gateway-app
docker cp ./backup-2026-04-28.db gateway-app:/app/data/gateway.db
docker-compose start gateway-app
```

### Logs y observabilidad

- **Stdout**: todos los logs van a stdout (formato `Microsoft.Extensions.Logging`).
- **Niveles**:
  - `Information` — eventos de negocio (webhook aceptado, tenant creado, túnel up).
  - `Warning` — webhook rechazado por firma, tenant inactivo, túnel sin configurar.
  - `Error` — errores de reenvío, DB, infraestructura.
- **Filtrado típico**:
  ```bash
  docker-compose logs -f gateway-app | grep -E "WARN|ERR"
  docker-compose logs -f gateway-app | grep "tenant"
  ```

### Limpieza de logs antiguos

La tabla `DeliveryLogs` puede crecer. Hoy no hay TTL automático — si lo necesitás, ejecutá manualmente:

```sql
DELETE FROM DeliveryLogs WHERE CreatedAt < datetime('now', '-90 days');
DELETE FROM DeliveryAttempts WHERE DeliveryLogId NOT IN (SELECT Id FROM DeliveryLogs);
VACUUM;
```

---

## 🗃️ Modelo de datos

```
┌──────────────┐       ┌──────────────┐
│ TenantGroup  │       │ RetryPolicy  │       ┌──────────────┐
│──────────────│       │──────────────│  1:N  │  RetryStep   │
│ Id           │       │ Id           │◄──────│──────────────│
│ Name         │◄──────│ Name         │       │ Id           │
│ RetryPolicyId│       │ IsDefault    │       │ RetryPolicyId│
│ CreatedAt    │       │ CB threshold │       │ StepNumber   │
└──────┬───────┘       │ CreatedAt    │       │ DelayValue   │
       │ 1:N           └──────┬───────┘       │ DelayUnit    │
       ▼                      │ 1:N           └──────────────┘
┌──────────────────┐           ▼
│   Tenant         │   (FK opcional desde Tenant)
│──────────────────│
│ Id               │
│ Name             │
│ Slug (unique)    │
│ TargetUrl        │
│ WebhookSecret    │
│ SignatureScheme  │  ← enum: WooCommerce | GitHub | Generic | None
│ SignatureHeader  │  ← override opcional del header
│ IsActive         │
│ TenantGroupId?   │
│ RetryPolicyId?   │
│ CreatedAt        │
│ UpdatedAt?       │
└──────┬───────────┘
       │ 1:N
       ▼
┌──────────────────┐       ┌────────────────┐
│  DeliveryLog     │  1:N  │DeliveryAttempt │
│──────────────────│◄──────│────────────────│
│ Id (long)        │       │ Id             │
│ TenantId         │       │ DeliveryLogId  │
│ WebhookEventId   │       │ AttemptNumber  │
│ Status           │       │ HttpStatusCode │
│ Payload          │       │ DurationMs     │
│ SourceUrl?       │       │ ErrorMessage?  │
│ TargetUrl        │       │ IsManual       │
│ AttemptNumber    │       │ CreatedAt      │
│ CurrentStep      │       └────────────────┘
│ HttpStatusCode?  │
│ NextRetryAt?     │
│ DurationMs       │
│ CreatedAt        │
└──────────────────┘

┌──────────────┐
│  AppSetting  │   ← Cloudflare creds, API Key del Gateway
│──────────────│
│ Id           │
│ Key (unique) │
│ Value        │
│ UpdatedAt    │
└──────────────┘
```

**Enum `Status` en DeliveryLog**: `Pending`, `Success`, `Scheduled` (esperando reintento), `Failed` (definitivo).

---

## 🛡️ Seguridad

| Capa                  | Mecanismo                                                                             |
| --------------------- | ------------------------------------------------------------------------------------- |
| Validación de origen  | HMAC-SHA256 por tenant, esquema configurable, comparación timing-safe                 |
| API administrativa    | API Key estática global (`X-Gateway-Api-Key`), comparación timing-safe                |
| Anti-enumeration      | `/ingest` y `/api/tenants` devuelven `401`/`404` sin filtrar info de tenants          |
| Aislamiento tenant    | Cada tenant opera con su propio secret, target, política                              |
| Transporte            | HTTPS automático vía Cloudflare Tunnel; tráfico interno plain HTTP                    |
| Persistencia secrets  | Secrets en SQLite (encriptable con SEE/SQLCipher si tu deploy lo requiere)            |
| Circuit breaker       | Polly evita cascadas de errores cuando un destino cae                                 |
| Hardcoded creds       | Cero. Todo via dashboard / env vars / DB                                              |

**Recomendaciones operativas:**
- Rotar API Key cuando un colaborador deja el equipo.
- Rotar `webhookSecret` por tenant si sospechás compromiso (regenerar el tenant + actualizar el origen).
- Usar volumen para `DataProtection-Keys` si tenés sesiones largas en el dashboard.

---

## 🔍 Troubleshooting

### El webhook no llega al destino

1. **Tenant activo**: dashboard → Tenants → confirmar chip "Activo".
2. **URL accesible**: desde la red del Gateway, `curl -i https://destino/hook`.
3. **Detalle del intento**: dashboard → Monitor → click en el webhook → mirá HTTP code y `ErrorMessage` del último intento.
4. **Firma**: si todos llegan rechazados con 401, casi seguro hay desalineación de secret o esquema.

### Webhook rechazado (401 / firma inválida)

Causas en orden de probabilidad:

1. **Secret desfasado** entre origen y tenant (el más común — un copy/paste con espacios al final, etc.).
2. **Esquema mal seteado**: el origen es GitHub pero el tenant está en `WooCommerce` (o viceversa).
3. **Header equivocado**: con `Generic`, el origen envía la firma en un header distinto al configurado en `signatureHeader`.
4. **Encoding del body**: si tu origen reformatea el JSON antes de firmar, la firma deja de coincidir. La firma debe calcularse sobre el body **exacto** que se envía por la red.

Para debuggear, podés temporalmente poner `signatureScheme: "None"` en el tenant y ver si los webhooks llegan al destino, después volver a activar el esquema correcto.

### `/api/tenants` responde 401

1. ¿Estás mandando el header `X-Gateway-Api-Key`?
2. ¿La key actual coincide? Verificala en dashboard → **Configuración**.
3. Si la rotaste, los sistemas externos siguen usando la vieja: actualizalos.

### Redis no conecta

```bash
docker exec gateway-redis redis-cli ping            # debería responder PONG
docker-compose logs gateway-redis                   # logs del servicio
docker exec gateway-app ping -c 2 gateway-redis     # conectividad de red
```

Con `AbortOnConnectFail = false`, la app no crashea si Redis no está al arranque, pero los webhooks se rechazarán al intentar encolar. Verificá que Redis esté `Up` y que el connection string apunte al hostname correcto (`gateway-redis:6379` en compose, `localhost:6380` en dev local).

### El túnel Cloudflare no se conecta

1. Confirmá los **3 IDs** (Account/Zone) y que el **API Token** tenga los permisos correctos.
2. Mirá los logs de la app: `docker-compose logs gateway-app | grep -i tunnel`. Errores comunes: token sin permiso DNS, zone ID equivocado, nombre de túnel ya en uso en la cuenta.
3. Si es la primera vez, esperá ~30 segundos a que Cloudflare propague el DNS antes de probar.

### "PendingModelChangesWarning" en logs de migración

Es benigno. Viene del seed de `RetryPolicy` con `CreatedAt = DateTime.UtcNow` que cambia entre builds. No afecta funcionamiento. Para silenciarlo definitivamente, hardcodear la fecha en el seed.

### El antiforgery token no se puede deserializar

```
The key {...} was not found in the key ring.
```

Las DataProtection keys se perdieron al recrear el contenedor. **Refrescá el browser** y se regeneran. Para evitarlo permanentemente, agregá un volumen para `/home/app/.aspnet/DataProtection-Keys` en `docker-compose.yml`.

### Reconstruir desde cero (¡destructivo!)

```bash
docker-compose down -v                # elimina volúmenes (DB y Redis)
docker-compose up -d --build
# La DB se recrea vacía; al primer arranque se genera nueva API Key (mirar logs).
```

---

## 📁 Estructura del repositorio

```
webhooks-gateway/
├── docker-compose.yml             # Orquestación Docker (app + Redis)
├── Dockerfile                     # Build multi-stage .NET 9
├── Dotar.Gateway.slnx             # Solución .NET
├── README.md                      # Este archivo
├── LICENSE                        # MIT
│
├── src/Dotar.Gateway/             # Proyecto principal (namespace Dotar.Gateway)
│   ├── Program.cs                 # Bootstrap + DI
│   ├── appsettings.json           # Config base
│   │
│   ├── Dashboard/Components/      # UI Blazor + MudBlazor
│   │   ├── App.razor
│   │   ├── Layout/MainLayout.razor
│   │   └── Pages/
│   │       ├── Home.razor
│   │       ├── Tenants.razor
│   │       ├── TenantGroups.razor
│   │       ├── RetryPolicies.razor
│   │       ├── Monitor.razor
│   │       ├── WebhookDetail.razor
│   │       └── Configuracion.razor
│   │
│   ├── Domain/
│   │   ├── Entities/              # Tenant, TenantGroup, RetryPolicy, RetryStep,
│   │   │                          # DeliveryLog, DeliveryAttempt, AppSetting,
│   │   │                          # SignatureScheme (enum)
│   │   └── Models/                # QueuedWebhook, ForwardResult
│   │
│   ├── Endpoints/                 # Minimal API
│   │   ├── IngestEndpoints.cs     # POST /ingest/{slug}
│   │   ├── TenantApiEndpoints.cs  # CRUD via API
│   │   └── ApiKeyEndpointFilter.cs
│   │
│   ├── Infrastructure/
│   │   ├── Data/GatewayDbContext.cs   # EF Core + SQLite WAL
│   │   ├── Services/
│   │   │   ├── HmacSignatureValidator.cs   # HMAC multi-esquema
│   │   │   ├── ApiKeyService.cs            # API Key auto-gen + rotate
│   │   │   ├── TenantCacheService.cs       # Cache in-memory de tenants
│   │   │   ├── RedisQueueService.cs
│   │   │   ├── ForwardingService.cs        # POST + Polly
│   │   │   └── MonitorNotificationService.cs
│   │   └── Tunnel/
│   │       ├── CloudflareConfig.cs
│   │       ├── CloudflareTunnelManager.cs  # API REST + cloudflared
│   │       └── TunnelStatusService.cs
│   │
│   ├── Workers/
│   │   ├── WebhookDispatcherWorker.cs   # Background dispatcher
│   │   └── TunnelStartupService.cs      # Auto-conexión del túnel
│   │
│   └── Migrations/                # EF Core (auto-apply al arrancar)
│
└── tests/Dotar.Gateway.Tests/     # xUnit + WebApplicationFactory
    ├── HmacSignatureValidatorTests.cs
    ├── TenantApiEndpointsTests.cs
    └── GatewayWebApplicationFactory.cs
```

> **Nota sobre el namespace**: el namespace .NET (`Dotar.Gateway`) se mantiene como identidad técnica del proyecto. El producto distribuido es **Webhooks Gateway**.

---

## 📝 Licencia

MIT License — ver [LICENSE](LICENSE).

---

<div align="center">

Desarrollado con ❤️ por [Dotar Soluciones](https://dotarsoluciones.com)

</div>
