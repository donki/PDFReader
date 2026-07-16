[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$TargetFramework,
    [string]$DotnetPath,
    [string]$KeystorePath,
    [string]$KeyAlias = 'smsforwarder',
    [string]$StorePass,
    [string]$KeyPass,
    [string]$IntermediateOutputRoot,
    [switch]$Clean,
    [switch]$SkipApk,
    [switch]$SkipAab,
    [switch]$SkipClean,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = $PSScriptRoot
$ProjectPath = Join-Path $ProjectRoot 'PDFReader.csproj'

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

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ErrorMessage,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # Las contrasenas nunca se imprimen (constitucion, seccion 6): la consola y los
    # logs de CI son material que se comparte.
    $shown = $Arguments | ForEach-Object {
        if ($_ -match '^-p:(AndroidSigningKeyPass|AndroidSigningStorePass)=') {
            ($_ -split '=', 2)[0] + '=***'
        }
        else {
            $_
        }
    }
    Write-Host "> $FilePath $($shown -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError $ErrorMessage
    }
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

function Resolve-ToolPath {
    param(
        [string]$ConfiguredPath,
        [Parameter(Mandatory = $true)][string]$CommandName,
        [string]$FallbackPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if (Test-Path -LiteralPath $ConfiguredPath) {
            return (Resolve-Path -LiteralPath $ConfiguredPath).Path
        }

        Exit-WithError "No existe $CommandName en: $ConfiguredPath"
    }

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($FallbackPath) -and (Test-Path -LiteralPath $FallbackPath)) {
        return $FallbackPath
    }

    Exit-WithError "No se encontro $CommandName. Pasalo con -DotnetPath."
}

function Get-LatestPackage {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Filter,
        [switch]$PreferPublishFolder
    )

    $items = Get-ChildItem -LiteralPath $Root -Filter $Filter -Recurse -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { if ($PreferPublishFolder -and $_.DirectoryName -like '*\publish') { 0 } else { 1 } } }, LastWriteTime -Descending

    return $items | Select-Object -First 1
}

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    Exit-WithError "No existe el proyecto: $ProjectPath"
}

$projectXml = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
$packageName = Get-ProjectValue $projectXml 'ApplicationId'
if ([string]::IsNullOrWhiteSpace($packageName)) {
    Exit-WithError 'No se encontro ApplicationId en el csproj.'
}

$displayVersion = Get-ProjectValue $projectXml 'ApplicationDisplayVersion'
$versionCode = Get-ProjectValue $projectXml 'ApplicationVersion'

if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
    $targetFrameworks = Get-ProjectValue $projectXml 'TargetFrameworks'
    $targetFramework = Get-ProjectValue $projectXml 'TargetFramework'
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($targetFrameworks)) {
        $candidates += $targetFrameworks.Split(';') | ForEach-Object { $_.Trim() }
    }
    if (-not [string]::IsNullOrWhiteSpace($targetFramework)) {
        $candidates += $targetFramework.Trim()
    }

    $TargetFramework = $candidates | Where-Object { $_ -like '*-android*' } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
    Exit-WithError 'No se encontro un TargetFramework Android en el csproj.'
}

$DotnetPath = Resolve-ToolPath $DotnetPath 'dotnet' 'C:\Program Files\dotnet\dotnet.exe'

if ([string]::IsNullOrWhiteSpace($KeystorePath)) {
    $KeystorePath = if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_KEYSTORE_PATH)) {
        $env:ANDROID_KEYSTORE_PATH
    }
    else {
        Join-Path $ProjectRoot 'socratic.keystore'
    }
}
$KeystorePath = if ([System.IO.Path]::IsPathRooted($KeystorePath)) { $KeystorePath } else { Join-Path $ProjectRoot $KeystorePath }
if (-not (Test-Path -LiteralPath $KeystorePath)) {
    Exit-WithError "No existe el keystore: $KeystorePath"
}
$KeystorePath = (Resolve-Path -LiteralPath $KeystorePath).Path

# La contrasena nunca se versiona (constitucion, seccion 6): llega por parametro,
# por ANDROID_KEYSTORE_PASSWORD o por keystore.password.txt, que esta en .gitignore.
if ([string]::IsNullOrWhiteSpace($StorePass)) {
    $passwordFile = Join-Path $ProjectRoot 'keystore.password.txt'
    $StorePass = if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_KEYSTORE_PASSWORD)) {
        $env:ANDROID_KEYSTORE_PASSWORD
    }
    elseif (Test-Path -LiteralPath $passwordFile) {
        (Get-Content -LiteralPath $passwordFile -Raw).Trim()
    }
    else {
        Exit-WithError 'No hay contrasena del keystore. Pasala con -StorePass, en ANDROID_KEYSTORE_PASSWORD o en keystore.password.txt.'
    }
}
if ([string]::IsNullOrWhiteSpace($KeyPass)) {
    $KeyPass = if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_KEY_PASSWORD)) {
        $env:ANDROID_KEY_PASSWORD
    }
    else {
        $StorePass
    }
}

$outputPath = Join-Path $ProjectRoot "bin\$Configuration\$TargetFramework"
if ([string]::IsNullOrWhiteSpace($IntermediateOutputRoot)) {
    $IntermediateOutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'PDFReader-msbuild-obj'
}
$IntermediateOutputRoot = if ([System.IO.Path]::IsPathRooted($IntermediateOutputRoot)) { $IntermediateOutputRoot } else { Join-Path $ProjectRoot $IntermediateOutputRoot }
New-Item -ItemType Directory -Path $IntermediateOutputRoot -Force | Out-Null
$IntermediateOutputRoot = (Resolve-Path -LiteralPath $IntermediateOutputRoot).Path
if (-not $IntermediateOutputRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar) -and -not $IntermediateOutputRoot.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
    $IntermediateOutputRoot = "$IntermediateOutputRoot$([System.IO.Path]::DirectorySeparatorChar)"
}

$intermediateArgs = @(
    "-p:BaseIntermediateOutputPath=$IntermediateOutputRoot",
    "-p:MSBuildProjectExtensionsPath=$IntermediateOutputRoot",
    '-p:DefaultItemExcludes="$(DefaultItemExcludes);bin\**;obj\**"'
)
$signingArgs = @(
    '-p:AndroidKeyStore=true',
    "-p:AndroidSigningKeyStore=$KeystorePath",
    "-p:AndroidSigningKeyAlias=$KeyAlias",
    "-p:AndroidSigningKeyPass=$KeyPass",
    "-p:AndroidSigningStorePass=$StorePass"
)

Write-Host '========================================'
Write-Host '   PDF Reader - Build and Sign Script'
Write-Host '========================================'
Write-Host
Write-Host "Proyecto: $ProjectPath"
Write-Host "TargetFramework: $TargetFramework"
Write-Host "Package: $packageName"
Write-Host "Version: $displayVersion (versionCode $versionCode)"
Write-Host "Keystore: $KeystorePath (alias $KeyAlias)"
Write-Host "Obj temporal: $IntermediateOutputRoot"
Write-Host

Push-Location $ProjectRoot
try {
    if ($Clean -and -not $SkipClean) {
        Write-Host '[1/4] Limpiando proyecto...'
        Invoke-NativeCommand 'Fallo al limpiar el proyecto' $DotnetPath (@('clean', $ProjectPath, '-c', $Configuration, '-f', $TargetFramework) + $intermediateArgs)
    }
    else {
        Write-Host '[1/4] Limpieza omitida. Usa -Clean si quieres forzar dotnet clean.'
    }

    if (-not $SkipApk) {
        Write-Host
        Write-Host '[2/4] Compilando APK firmado para validacion en dispositivo...'
        # EmbedAssembliesIntoApk: obligatorio en cualquier despliegue (constitucion, anexo A.7).
        Invoke-NativeCommand 'Fallo al compilar APK' $DotnetPath (@(
            'publish', $ProjectPath,
            '-f', $TargetFramework,
            '-c', $Configuration,
            '-p:AndroidPackageFormat=apk',
            '-p:EmbedAssembliesIntoApk=true'
        ) + $intermediateArgs + $signingArgs)
    }
    else {
        Write-Host
        Write-Host '[2/4] APK omitido.'
    }

    if (-not $SkipAab) {
        Write-Host
        Write-Host '[3/4] Compilando AAB firmado para Google Play...'
        Invoke-NativeCommand 'Fallo al compilar AAB' $DotnetPath (@(
            'publish', $ProjectPath,
            '-f', $TargetFramework,
            '-c', $Configuration,
            '-p:AndroidPackageFormat=aab',
            '-p:EmbedAssembliesIntoApk=true'
        ) + $intermediateArgs + $signingArgs)
    }
    else {
        Write-Host
        Write-Host '[3/4] AAB omitido.'
    }

    Write-Host
    Write-Host '[4/4] Verificando archivos generados...'

    $signedApk = $null
    if (-not $SkipApk -and (Test-Path -LiteralPath $outputPath)) {
        $signedApk = Get-LatestPackage $outputPath '*-Signed.apk' -PreferPublishFolder
    }

    $signedAab = $null
    if (-not $SkipAab -and (Test-Path -LiteralPath $outputPath)) {
        $signedAab = Get-LatestPackage $outputPath '*-Signed.aab' -PreferPublishFolder
    }

    if ($signedApk) {
        Write-Host "OK APK firmado: $($signedApk.FullName)"
        Write-Host "  Tamano: $($signedApk.Length) bytes"
        Write-Host "  SHA-256: $((Get-FileHash -LiteralPath $signedApk.FullName -Algorithm SHA256).Hash)"
    }
    elseif (-not $SkipApk) {
        Exit-WithError 'APK firmado no encontrado.'
    }

    if ($signedAab) {
        Write-Host "OK AAB firmado: $($signedAab.FullName)"
        Write-Host "  Tamano: $($signedAab.Length) bytes"
        Write-Host "  SHA-256: $((Get-FileHash -LiteralPath $signedAab.FullName -Algorithm SHA256).Hash)"
    }
    elseif (-not $SkipAab) {
        Exit-WithError 'AAB firmado no encontrado.'
    }

    Write-Host
    Write-Host '========================================'
    Write-Host '          COMPILACION COMPLETA'
    Write-Host '========================================'
    if ($signedApk) {
        Write-Host "Instalar en dispositivo: .\install_device.ps1 -Launch"
    }
    if ($signedAab) {
        Write-Host "AAB para Google Play: $($signedAab.FullName)"
    }
    Pause-Script
}
finally {
    Pop-Location
}
