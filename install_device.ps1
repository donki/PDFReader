[CmdletBinding()]
param(
    [string]$ApkPath,
    [string]$AdbPath,
    [string]$DeviceSerial,
    [string]$PackageName,
    [switch]$BuildFirst,
    [switch]$Launch,
    [switch]$Mumu,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = $PSScriptRoot
$ProjectPath = Join-Path $ProjectRoot 'PDFReader.csproj'
$DefaultAdbPaths = @(
    'C:\Program Files\platform-tools\adb.exe',
    'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe',
    (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
    'C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe',
    'C:\Program Files\Netease\MuMuPlayer\nx_device\12.0\shell\adb.exe'
)
$MumuPorts = @('127.0.0.1:16384', '127.0.0.1:7555', '127.0.0.1:16416', '127.0.0.1:5555')

function Pause-Script {
    if (-not $NoPause) {
        Read-Host 'Pulsa Enter para continuar' | Out-Null
    }
}

function Exit-WithError {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "ERROR: $Message" -ForegroundColor Red
    Pause-Script
    exit 1
}

function Get-ProjectValue {
    param(
        [Parameter(Mandatory = $true)][xml]$ProjectXml,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nodes = $ProjectXml.SelectNodes("//*[local-name()='$Name']")
    foreach ($node in $nodes) {
        if (-not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText.Trim()
        }
    }

    return $null
}

function Resolve-AdbPath {
    if (-not [string]::IsNullOrWhiteSpace($AdbPath)) {
        if (Test-Path -LiteralPath $AdbPath) {
            return (Resolve-Path -LiteralPath $AdbPath).Path
        }

        Exit-WithError "No existe adb.exe en: $AdbPath"
    }

    foreach ($candidate in $DefaultAdbPaths) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    Exit-WithError 'No se encontro adb.exe. Pasa la ruta con -AdbPath.'
}

function Get-AdbDevices {
    $raw = & $AdbPath devices
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError 'No se pudo listar dispositivos ADB.'
    }

    return $raw |
        Where-Object { $_ -match '^\S+\s+device$' } |
        ForEach-Object { ($_ -split '\s+')[0] }
}

function Resolve-DeviceSerial {
    $devices = @(Get-AdbDevices)

    if (-not [string]::IsNullOrWhiteSpace($DeviceSerial)) {
        if ($devices -notcontains $DeviceSerial) {
            Write-Host 'Dispositivos disponibles:'
            if ($devices.Count -eq 0) { Write-Host '  (ninguno)' } else { $devices | ForEach-Object { Write-Host "  $_" } }
            Exit-WithError "El dispositivo '$DeviceSerial' no esta conectado o no ha autorizado la depuracion USB."
        }

        return $DeviceSerial
    }

    if ($Mumu) {
        foreach ($port in $MumuPorts) {
            if ($devices -contains $port) {
                return $port
            }
        }

        foreach ($port in $MumuPorts) {
            Write-Host "Intentando conectar a MuMu en $port..."
            & $AdbPath connect $port | Out-Host
        }

        $devices = @(Get-AdbDevices)
        foreach ($port in $MumuPorts) {
            if ($devices -contains $port) {
                return $port
            }
        }

        Exit-WithError 'No se detecto MuMu por ADB. Abre MuMu Player y vuelve a ejecutar el script.'
    }

    # Sin -Mumu se prefiere un dispositivo fisico: los puertos de MuMu son emuladores.
    $physical = @($devices | Where-Object { $_ -notmatch '^(127\.0\.0\.1:|emulator-)' })
    if ($physical.Count -eq 1) {
        return $physical[0]
    }

    if ($physical.Count -gt 1) {
        Write-Host 'Dispositivos fisicos detectados:'
        $physical | ForEach-Object { Write-Host "  $_" }
        Exit-WithError 'Hay varios dispositivos fisicos. Usa -DeviceSerial para elegir uno.'
    }

    if ($devices.Count -eq 1) {
        return $devices[0]
    }

    Exit-WithError 'No se detecto ningun dispositivo. Conecta el telefono con depuracion USB o usa -Mumu.'
}

function Get-LatestSignedApk {
    $items = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'bin') -Filter '*-Signed.apk' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    return $items | Select-Object -First 1
}

function Build-Apk {
    $buildScript = Join-Path $ProjectRoot 'build_and_sign.ps1'
    if (-not (Test-Path -LiteralPath $buildScript)) {
        Exit-WithError 'No existe build_and_sign.ps1 para compilar antes de instalar.'
    }

    & $buildScript -NoPause -SkipAab
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError 'Fallo el build del APK.'
    }
}

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    Exit-WithError "No existe el proyecto: $ProjectPath"
}

$projectXml = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = Get-ProjectValue $projectXml 'ApplicationId'
}
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    Exit-WithError 'No se pudo resolver ApplicationId desde el csproj.'
}

$AdbPath = Resolve-AdbPath

Write-Host '========================================'
Write-Host '   PDF Reader - Instalar en dispositivo'
Write-Host '========================================'
Write-Host
Write-Host "ADB: $AdbPath"
Write-Host "Package: $PackageName"

if ($BuildFirst) {
    Write-Host
    Write-Host 'Compilando APK antes de instalar...'
    Build-Apk
}

if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $latestApk = Get-LatestSignedApk
    if (-not $latestApk) {
        Exit-WithError 'No se encontro ningun *-Signed.apk. Ejecuta con -BuildFirst o compila primero.'
    }
    $ApkPath = $latestApk.FullName
}
else {
    $ApkPath = if ([System.IO.Path]::IsPathRooted($ApkPath)) { $ApkPath } else { Join-Path (Get-Location) $ApkPath }
}

if (-not (Test-Path -LiteralPath $ApkPath)) {
    Exit-WithError "No existe el APK: $ApkPath"
}
$ApkPath = (Resolve-Path -LiteralPath $ApkPath).Path
$apkItem = Get-Item -LiteralPath $ApkPath

$DeviceSerial = Resolve-DeviceSerial
$model = (& $AdbPath -s $DeviceSerial shell getprop ro.product.model).Trim()
Write-Host "Dispositivo: $DeviceSerial ($model)"
Write-Host "APK: $ApkPath"
Write-Host "Tamano: $($apkItem.Length) bytes"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash)"
Write-Host

# -r reinstala conservando datos; -d permite bajar de versionCode durante las pruebas.
Write-Host 'Instalando...'
$installOutput = & $AdbPath -s $DeviceSerial install -r -d $ApkPath 2>&1
$installOutput | ForEach-Object { Write-Host $_ }

if ($installOutput -match 'INSTALL_FAILED_USER_RESTRICTED') {
    Write-Host
    Write-Host 'El dispositivo rechazo la instalacion por USB (bloqueo tipico de Xiaomi/HyperOS).' -ForegroundColor Yellow
    Write-Host 'Activa: Ajustes > Opciones de desarrollador > Instalar via USB.' -ForegroundColor Yellow
    Exit-WithError 'Instalacion bloqueada por el dispositivo.'
}

if ($installOutput -notmatch 'Success') {
    Exit-WithError 'Fallo la instalacion del APK.'
}

if ($Launch) {
    Write-Host
    Write-Host 'Lanzando app...'
    # am start con la actividad real: monkey inyecta un evento aleatorio y falsea
    # la pantalla que se valida (constitucion, anexo A.8.1).
    $resolved = & $AdbPath -s $DeviceSerial shell cmd package resolve-activity --brief $PackageName
    $activity = $resolved | Where-Object { $_ -match "^$([regex]::Escape($PackageName))/" } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($activity)) {
        Exit-WithError 'La app se instalo, pero no se pudo resolver la actividad de lanzamiento.'
    }

    & $AdbPath -s $DeviceSerial shell am start -n $activity.Trim() | Out-Host

    Start-Sleep -Seconds 3
    $fatal = & $AdbPath -s $DeviceSerial logcat -d -t 200 2>&1 | Select-String -Pattern 'FATAL EXCEPTION'
    if ($fatal) {
        Write-Host
        Write-Host 'ATENCION: hay una excepcion no controlada en el arranque (constitucion, seccion 10):' -ForegroundColor Red
        $fatal | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    else {
        Write-Host 'Arranque sin excepciones registradas.'
    }
}

Write-Host
Write-Host "Instalacion completada en $model."
Pause-Script
