#requires -Version 7.0
<#
.SYNOPSIS
    Bumpea la versión semver desde Conventional Commits, actualiza version.json,
    lo commitea y despliega con Docker Compose al contexto remoto.

.DESCRIPTION
    Flujo:
      1. Lee version.json (versión actual + último commit deployado).
      2. Analiza los commits desde el último deploy (Conventional Commits) para
         decidir el bump: major / minor / patch, y arma el changelog.
      3. Calcula la nueva versión y antepone la entrada al historial.
      4. Commitea version.json con "chore(release): bump version to X.Y.Z".
      5. docker compose up -d --build (contexto Docker remoto).

    NO hace git push: el commit queda local hasta que vos lo subas.
    NO usa "down -v": jamás toca los volúmenes con la DB/cola.

.PARAMETER Context
    Contexto Docker destino. Default: laboratorio (ssh://lab-oficina).

.PARAMETER DeployHost
    Etiqueta del host que se guarda en version.json. Default: lab-oficina.

.PARAMETER Force
    Permite deployar aunque no haya commits nuevos (rebuild de la versión actual,
    sin bump ni commit).

.PARAMETER SkipDeploy
    Solo bumpea y commitea version.json; no ejecuta docker compose.

.PARAMETER Novedades
    Lista de novedades en lenguaje de negocio para mostrar en /historial-deploys.
    Cada elemento es una cadena descriptiva orientada al operador o al usuario final.
    Ejemplo: .\deploy.ps1 -Novedades "Se habilitó el pago con QR","Mejora de rendimiento en logs"

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -SkipDeploy
    .\deploy.ps1 -Force
    .\deploy.ps1 -Novedades "Nueva feature X","Corrección en Y"
#>
[CmdletBinding()]
param(
    [string]$Context = "laboratorio",
    [string]$DeployHost = "lab-oficina",
    [switch]$Force,
    [switch]$SkipDeploy,
    [string[]]$Novedades = @()
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$versionPath = Join-Path $projectRoot "version.json"

function Write-Step { param([string]$Msg) Write-Host "==> $Msg" -ForegroundColor Cyan }
function Write-Warn { param([string]$Msg) Write-Host "[!] $Msg" -ForegroundColor Yellow }

# ─── 1. Leer version.json ────────────────────────────────────────────────
if (-not (Test-Path $versionPath)) {
    throw "No se encontró version.json en $versionPath"
}
$versionData    = Get-Content $versionPath -Raw | ConvertFrom-Json
$currentVersion = $versionData.current
$lastCommit     = $versionData.history[0].gitCommit
Write-Step "Versión actual: $currentVersion (último deploy: $($lastCommit.Substring(0,[Math]::Min(10,$lastCommit.Length))))"

# ─── 2. Analizar commits (Conventional Commits) ──────────────────────────
$currentCommit = (git rev-parse HEAD | Out-String).Trim()
$bumpType  = "patch"   # default
$changelog = ""

if ($currentCommit -eq $lastCommit) {
    if (-not $Force) {
        Write-Warn "No hay commits nuevos desde el último deploy. Usá -Force para reconstruir la versión actual sin bump."
        return
    }
    Write-Warn "Sin commits nuevos: se reconstruye $currentVersion sin bump (modo -Force)."
}
else {
    $commits = @(git log "$lastCommit..HEAD" --format="%s")

    foreach ($c in $commits) {
        if ($c -match 'BREAKING[\s_]CHANGE|^feat!:|^fix!:|^\w+!:') {
            $bumpType = "major"; break
        }
        if ($c -match '^feat[\(:]' -and $bumpType -ne "major") {
            $bumpType = "minor"
        }
    }

    # Changelog auto-generado desde los subjects.
    $lines = $commits | ForEach-Object {
        $prefix = switch -Regex ($_) {
            '^feat'                     { "[FEAT]" }
            '^fix'                      { "[FIX]" }
            '^refactor'                 { "[REFACTOR]" }
            '^perf'                     { "[PERF]" }
            '^chore|^docs|^style|^test' { "[CHORE]" }
            default                     { "[*]" }
        }
        "$prefix $_"
    }
    $changelog = $lines -join " | "
}

# ─── 3. Calcular nueva versión ───────────────────────────────────────────
if ($currentCommit -eq $lastCommit -and $Force) {
    $newVersion = $currentVersion   # rebuild sin bump
}
else {
    $parts = $currentVersion -split '\.'
    $major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
    $newVersion = switch ($bumpType) {
        "major" { "$($major + 1).0.0" }
        "minor" { "$major.$($minor + 1).0" }
        default { "$major.$minor.$($patch + 1)" }
    }
    Write-Step "Bump: $bumpType  →  $currentVersion → $newVersion"
}

# ─── 4. Actualizar version.json (si hubo bump) ───────────────────────────
$didBump = ($newVersion -ne $currentVersion)
if ($didBump) {
    $newEntry = [PSCustomObject]@{
        version    = $newVersion
        builtAt    = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
        deployedBy = $env:USERNAME
        host       = $DeployHost
        bumpType   = $bumpType
        gitCommit  = $currentCommit
        changes    = $changelog
        novedades  = @($Novedades)
    }
    $versionData.current = $newVersion
    $versionData.history = @($newEntry) + @($versionData.history)
    $versionData | ConvertTo-Json -Depth 10 | Set-Content $versionPath -Encoding UTF8
    Write-Step "version.json actualizado a $newVersion"

    # ─── 5. Commit del version.json ──────────────────────────────────────
    git add $versionPath
    git commit -m "chore(release): bump version to $newVersion"
    Write-Step "Commit creado (no se hizo push)."
}

# ─── 6. Deploy con Docker Compose ────────────────────────────────────────
if ($SkipDeploy) {
    Write-Warn "SkipDeploy activo: no se ejecuta docker compose."
    return
}

# Verificar contexto Docker activo.
$activeContext = (docker context show | Out-String).Trim()
if ($activeContext -ne $Context) {
    Write-Warn "Contexto Docker activo = '$activeContext', se esperaba '$Context'."
    Write-Step "Cambiando a contexto '$Context'..."
    docker context use $Context | Out-Null
}

Write-Step "Desplegando: docker compose up -d --build"
# BuildKit no tolera el daemon remoto vía SSH ("failed to list workers / write client
# preface"). Forzamos el builder clásico, que transmite el contexto al daemon remoto.
$env:DOCKER_BUILDKIT = "0"
$env:COMPOSE_DOCKER_CLI_BUILD = "1"
# SSH al server es intermitente: reintentar una vez ante fallo de handshake.
docker compose up -d --build
if ($LASTEXITCODE -ne 0) {
    Write-Warn "Primer intento falló (¿SSH intermitente?). Reintentando una vez..."
    Start-Sleep -Seconds 3
    docker compose up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose falló tras el reintento (exit $LASTEXITCODE)." }
}

Write-Step "Deploy de v$newVersion completado."
