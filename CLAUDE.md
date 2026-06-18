# Webhooks Gateway — instrucciones de proyecto

## Despliegue
- **Docker context activo**: `laboratorio` → `ssh://lab-oficina` (server remoto). Verificá con `docker context ls` antes de cualquier `docker compose up/down`.
- Build & deploy: `docker compose up -d --build` desde la raíz del proyecto.
- La SSH al server es intermitente. Si falla por `kex_exchange_identification: Connection closed by remote host`, reintentá una vez antes de escalar.

## Túnel y URLs
- Túnel Cloudflare: `https://webhook-gateway.dotarsoluciones.com` (HTTPS automático).
- Dashboard local: `http://localhost:8082` (puerto del host → 5200 del container).
- Endpoint público de ingesta: `POST /ingest/{slug}` (validación HMAC según `SignatureScheme` del tenant).
- Páginas internas: `/tenants`, `/grupos`, `/politicas`, `/monitor`, `/logs`, `/configuracion`.

## Convenciones
- Secrets autogenerados (API Key del Gateway + WebhookSecret de tenants) van en **base64**, no hex. Tenants viejos en hex siguen funcionando — no rotar sin permiso.
- Código, comentarios, mensajes de UI y commits en **español**.
- Antes de pushear o cambiar config compartida, confirmar con el usuario.
- Build/deploy/migraciones EF Core: `dotnet build src/Dotar.Gateway/Dotar.Gateway.csproj` y `dotnet ef migrations add <Nombre> --project src/Dotar.Gateway/Dotar.Gateway.csproj`.

## Arquitectura mínima
- .NET 9, Blazor Server + MudBlazor v9, EF Core + SQLite (WAL), Redis (cola), Polly v8 (CB).
- Persistencia: `gateway.db` en `/app/data` (volumen `gateway-app-data`). Cola: Redis (`gateway-redis:6379` interno).
- Logs estructurados: tabla `SystemLogs` (categorías `Ingest`, `Forward`, `Retry`, `ManualRetry`, `Auth`, `Worker`, `Tunnel`, `Api`, `System`). Vista en `/logs`.

## Lo que NO hacer
- Nunca `--no-verify` ni `--no-gpg-sign` en commits.
- Nunca `docker compose down -v` (borra volúmenes con la DB y la cola).
- No commitear `gateway.db`, `*.bak`, ni archivos con credenciales.
- No tocar tenants productivos (ver memoria de proyecto) sin permiso.
- No proponer "init" de Claude Code (ya hay CLAUDE.md y memoria configurados).
