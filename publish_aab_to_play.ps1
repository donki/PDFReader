[CmdletBinding()]
param(
    [string]$ServiceAccountJson,
    [string]$PackageName,
    [string]$AabPath,
    [switch]$BuildFirst,
    [string]$BuildScriptPath,
    [string]$Track,
    [ValidateSet('draft', 'completed', 'inProgress')]
    [string]$Status,
    [string]$ReleaseName,
    [string]$ReleaseNotesLanguage = 'es-ES',
    [string]$ReleaseNotes,
    [string]$StoreIconPath,
    [string]$StoreListingLanguage = 'es-ES',
    [string]$StoreListingPath,
    [string]$StoreTitle,
    [string]$StoreShortDescription,
    [string]$StoreFullDescription,
    [switch]$SkipStoreListing,
    [switch]$SkipStoreIcon,
    [switch]$SkipAabUpload,
    [switch]$ReuseExistingVersionCode,
    [string]$ExistingVersionCode,
    [bool]$ErrorIfInReview = $true,
    [bool]$SendForReview = $true,
    [switch]$AssumeYes,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Este script necesita PowerShell 7 o superior por la firma JWT de la cuenta de servicio. Ejecutalo con: pwsh .\publish_aab_to_play.ps1'
}

$ApiRoot = 'https://androidpublisher.googleapis.com/androidpublisher/v3'
$UploadRoot = 'https://androidpublisher.googleapis.com/upload/androidpublisher/v3'
$AndroidPublisherScope = 'https://www.googleapis.com/auth/androidpublisher'
$ProjectRoot = $PSScriptRoot
$ProjectPath = (Get-ChildItem -LiteralPath $ProjectRoot -Filter '*.csproj' | Select-Object -First 1).FullName
$DefaultServiceAccountJson = if (-not [string]::IsNullOrWhiteSpace($env:GOOGLE_APPLICATION_CREDENTIALS)) {
    $env:GOOGLE_APPLICATION_CREDENTIALS
}
else {
    Join-Path $ProjectRoot 'hiker-433118-98861f2881fa.json'
}
$DefaultStoreIconPath = Join-Path $ProjectRoot 'Resources\AppIcon\play_store_icon.png'
$DefaultStoreListingPath = Join-Path $ProjectRoot 'PlayStoreListing.es-ES.json'
$DefaultTargetFramework = 'net9.0-android36.0'
$AccessToken = $null

function Write-Section {
    param([Parameter(Mandatory = $true)][string]$Title)

    Write-Host
    Write-Host "=== $Title ==="
}

function Read-Default {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$DefaultValue
    )

    if ($AssumeYes) { return $DefaultValue }

    if ([string]::IsNullOrWhiteSpace($DefaultValue)) {
        $value = Read-Host $Prompt
    }
    else {
        $value = Read-Host "$Prompt [$DefaultValue]"
    }

    if ($null -eq $value) {
        return $DefaultValue
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value.Trim().Trim('"')
}

function Read-YesNo {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [bool]$DefaultYes = $true
    )

    if ($AssumeYes) { return $DefaultYes }

    $suffix = if ($DefaultYes) { '[S/n]' } else { '[s/N]' }
    while ($true) {
        $rawValue = Read-Host "$Prompt $suffix"
        if ($null -eq $rawValue) {
            return $DefaultYes
        }

        $value = $rawValue.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $DefaultYes
        }

        switch -Regex ($value) {
            '^(s|si|y|yes)$' { return $true }
            '^(n|no)$' { return $false }
            default { Write-Host 'Responde S o N.' }
        }
    }
}

function Resolve-InputPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $cleanPath = $Path.Trim().Trim('"')
    if ([System.IO.Path]::IsPathRooted($cleanPath)) {
        return $cleanPath
    }

    return Join-Path (Get-Location) $cleanPath
}

function Get-ProjectValue {
    param(
        [Parameter(Mandatory = $true)][xml]$ProjectXml,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nodes = $ProjectXml.SelectNodes("//$Name")
    foreach ($node in $nodes) {
        if (-not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText.Trim()
        }
    }

    return $null
}

function ConvertTo-Base64Url {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertTo-Base64UrlJson {
    param([Parameter(Mandatory = $true)]$Value)

    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    return ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($json))
}

function Get-AccessTokenFromServiceAccount {
    param([Parameter(Mandatory = $true)][string]$JsonPath)

    $account = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($account.client_email) -or [string]::IsNullOrWhiteSpace($account.private_key)) {
        throw 'El JSON no parece ser una credencial de cuenta de servicio valida.'
    }

    $tokenUri = if ([string]::IsNullOrWhiteSpace($account.token_uri)) {
        'https://oauth2.googleapis.com/token'
    }
    else {
        $account.token_uri
    }

    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = [ordered]@{
        alg = 'RS256'
        typ = 'JWT'
    }
    $claim = [ordered]@{
        iss = $account.client_email
        scope = $AndroidPublisherScope
        aud = $tokenUri
        iat = $now
        exp = $now + 3600
    }

    $unsignedJwt = "$(ConvertTo-Base64UrlJson $header).$(ConvertTo-Base64UrlJson $claim)"
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem([string]$account.private_key)
        $signature = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsignedJwt),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
        )
    }
    finally {
        $rsa.Dispose()
    }

    $jwt = "$unsignedJwt.$(ConvertTo-Base64Url $signature)"
    $tokenResponse = Invoke-RestMethod `
        -Method Post `
        -Uri $tokenUri `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{
            grant_type = 'urn:ietf:params:oauth:grant-type:jwt-bearer'
            assertion = $jwt
        } `
        -TimeoutSec 120

    if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
        throw 'Google no devolvio access_token.'
    }

    return $tokenResponse.access_token
}

function Get-GoogleApiErrorText {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.ErrorDetails.Message)) {
        return $ErrorRecord.ErrorDetails.Message
    }

    return $ErrorRecord.Exception.Message
}

function Invoke-GoogleJsonApi {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('GET', 'POST', 'PUT', 'DELETE')][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [object]$Body,
        [int]$TimeoutSec = 120
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        TimeoutSec = $TimeoutSec
    }

    if ($PSBoundParameters.ContainsKey('Body')) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        throw "Google API error: $(Get-GoogleApiErrorText $_)"
    }
}

function Invoke-GoogleMediaUpload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string]$ContentType = 'application/octet-stream'
    )

    try {
        return Invoke-RestMethod `
            -Method Post `
            -Uri $Uri `
            -Headers $Headers `
            -InFile $FilePath `
            -ContentType $ContentType `
            -TimeoutSec 600
    }
    catch {
        throw "Google API upload error: $(Get-GoogleApiErrorText $_)"
    }
}

function Add-QueryString {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Query
    )

    $pairs = foreach ($key in $Query.Keys) {
        if ($null -ne $Query[$key]) {
            '{0}={1}' -f [Uri]::EscapeDataString($key), [Uri]::EscapeDataString([string]$Query[$key])
        }
    }

    if (-not $pairs) {
        return $Uri
    }

    return "$Uri`?$($pairs -join '&')"
}

function Escape-PathSegment {
    param([Parameter(Mandatory = $true)][string]$Value)

    return [Uri]::EscapeDataString($Value)
}

function Normalize-ListingText {
    param([string]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return ($Value -replace '\s+', ' ').Trim()
}

function Get-StoreListingConfig {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = $DefaultStoreListingPath
    }

    $Path = Resolve-InputPath $Path
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "No existe el fichero de ficha de Play Console: $Path"
    }

    $listing = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    return @{
        Path = $Path
        Language = [string]$listing.language
        Title = [string]$listing.title
        ShortDescription = [string]$listing.shortDescription
        FullDescription = [string]$listing.fullDescription
    }
}

function Test-StoreListingPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$ShortDescription,
        [Parameter(Mandatory = $true)][string]$FullDescription
    )

    $normalizedTitle = Normalize-ListingText $Title
    $normalizedShort = Normalize-ListingText $ShortDescription
    $normalizedFull = Normalize-ListingText $FullDescription

    if ([string]::IsNullOrWhiteSpace($normalizedTitle)) {
        throw 'El titulo de la ficha no puede estar vacio.'
    }
    if ([string]::IsNullOrWhiteSpace($normalizedShort)) {
        throw 'La descripcion corta de la ficha no puede estar vacia.'
    }
    if ([string]::IsNullOrWhiteSpace($normalizedFull)) {
        throw 'La descripcion completa de la ficha no puede estar vacia.'
    }
    if ($normalizedTitle.Length -gt 30) {
        throw "El titulo de Google Play debe tener 30 caracteres o menos. Actual: $($normalizedTitle.Length)."
    }
    if ($normalizedShort.Length -gt 80) {
        throw "La descripcion corta de Google Play debe tener 80 caracteres o menos. Actual: $($normalizedShort.Length)."
    }
    if ($normalizedFull.Length -gt 4000) {
        throw "La descripcion completa de Google Play debe tener 4000 caracteres o menos. Actual: $($normalizedFull.Length)."
    }
    if ($normalizedTitle.Equals($normalizedShort, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Metadata policy: el titulo y la descripcion corta no pueden ser identicos.'
    }
    if ($normalizedTitle.Equals($normalizedFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Metadata policy: el titulo y la descripcion completa no pueden ser identicos.'
    }
}

function Get-DefaultSignedAabPath {
    param([string]$DefaultPackageName)

    $releaseRoot = Join-Path $ProjectRoot 'bin\Release'
    if (Test-Path -LiteralPath $releaseRoot) {
        $latestFromPublish = Get-ChildItem -LiteralPath $releaseRoot -Filter '*-Signed.aab' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*\publish' } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($latestFromPublish) {
            return $latestFromPublish.FullName
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($DefaultPackageName)) {
        $expectedPaths = @(
            (Join-Path $ProjectRoot "bin\Release\$DefaultTargetFramework\publish\$DefaultPackageName-Signed.aab"),
            (Join-Path $ProjectRoot "bin\Release\$DefaultTargetFramework\$DefaultPackageName-Signed.aab")
        )

        foreach ($expected in $expectedPaths) {
            if (Test-Path -LiteralPath $expected) {
                return $expected
            }
        }
    }

    if (Test-Path -LiteralPath $releaseRoot) {
        $latest = Get-ChildItem -LiteralPath $releaseRoot -Filter '*-Signed.aab' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($latest) {
            return $latest.FullName
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($DefaultPackageName)) {
        return Join-Path $ProjectRoot "bin\Release\$DefaultTargetFramework\publish\$DefaultPackageName-Signed.aab"
    }

    return Join-Path $ProjectRoot "bin\Release\$DefaultTargetFramework\publish\app-Signed.aab"
}

function Read-Status {
    param([string]$DefaultStatus)

    if ([string]::IsNullOrWhiteSpace($DefaultStatus)) {
        $DefaultStatus = 'completed'
    }

    while ($true) {
        $value = Read-Default 'Estado release: draft, completed o inProgress' $DefaultStatus
        switch -Regex ($value) {
            '^draft$' { return 'draft' }
            '^completed$' { return 'completed' }
            '^inprogress$' { return 'inProgress' }
            default { Write-Host 'Valor no valido. Usa draft, completed o inProgress.' }
        }
    }
}

function Normalize-Status {
    param([Parameter(Mandatory = $true)][string]$Value)

    switch -Regex ($Value) {
        '^draft$' { return 'draft' }
        '^completed$' { return 'completed' }
        '^inprogress$' { return 'inProgress' }
        default { throw 'Estado release no valido. Usa draft, completed o inProgress.' }
    }
}

function Read-PercentAsFraction {
    param([string]$Prompt, [double]$DefaultPercent)

    while ($true) {
        $raw = Read-Default $Prompt ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $DefaultPercent))
        $normalized = $raw.Replace(',', '.')
        $percent = 0.0
        if ([double]::TryParse($normalized, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$percent)) {
            if ($percent -gt 0 -and $percent -lt 100) {
                return $percent / 100
            }
        }

        Write-Host 'Introduce un porcentaje mayor que 0 y menor que 100.'
    }
}

Write-Host '========================================'
Write-Host ' SMS Forwarder - Publish AAB to Play Console'
Write-Host '========================================'
Write-Host
Write-Host 'Necesitas una cuenta de servicio invitada en Play Console con permisos de release.'
Write-Host 'El script crea un edit, sube el AAB, actualiza el track y valida o hace commit.'

$projectXml = $null
if (Test-Path -LiteralPath $ProjectPath) {
    $projectXml = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
}

$defaultPackageName = if ($projectXml) { Get-ProjectValue $projectXml 'ApplicationId' } else { $null }
$DefaultTargetFramework = if ($projectXml) {
    $targetFrameworks = Get-ProjectValue $projectXml 'TargetFrameworks'
    if ([string]::IsNullOrWhiteSpace($targetFrameworks)) {
        $targetFramework = Get-ProjectValue $projectXml 'TargetFramework'
        if ([string]::IsNullOrWhiteSpace($targetFramework)) { $DefaultTargetFramework } else { $targetFramework }
    }
    else {
        $targetFrameworks.Split(';')[0].Trim()
    }
}
else {
    $DefaultTargetFramework
}
$defaultVersionCode = if ($projectXml) { Get-ProjectValue $projectXml 'ApplicationVersion' } else { $null }
$defaultDisplayVersion = if ($projectXml) { Get-ProjectValue $projectXml 'ApplicationDisplayVersion' } else { $null }
$defaultReleaseName = if ($defaultDisplayVersion -and $defaultVersionCode) {
    "SMS Forwarder $defaultDisplayVersion ($defaultVersionCode)"
}
elseif ($defaultVersionCode) {
    "SMS Forwarder ($defaultVersionCode)"
}
else {
    "SMS Forwarder $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
}

Write-Section 'Datos de la app'
if (-not $PackageName) {
    $PackageName = Read-Default 'Package name' $defaultPackageName
}
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    throw 'Package name requerido.'
}

if ($BuildFirst -and -not $SkipAabUpload) {
    if ([string]::IsNullOrWhiteSpace($BuildScriptPath)) {
        $BuildScriptPath = Join-Path $ProjectRoot 'build_and_sign.ps1'
    }
    $BuildScriptPath = Resolve-InputPath $BuildScriptPath
    if (-not (Test-Path -LiteralPath $BuildScriptPath)) {
        throw "No existe el script de build: $BuildScriptPath"
    }

    Write-Section 'Build previo'
    & $BuildScriptPath -SkipApk -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw 'Fallo el build previo.'
    }
}

if ($SkipAabUpload) {
    if (-not $ExistingVersionCode) {
        $ExistingVersionCode = $defaultVersionCode
    }
    if ([string]::IsNullOrWhiteSpace($ExistingVersionCode)) {
        throw 'ExistingVersionCode requerido cuando se usa SkipAabUpload.'
    }
}
else {
    if (-not $AabPath) {
        $AabPath = Read-Default 'Ruta del AAB firmado' (Get-DefaultSignedAabPath $PackageName)
    }
    $AabPath = Resolve-InputPath $AabPath
    if (-not (Test-Path -LiteralPath $AabPath)) {
        throw "No existe el AAB: $AabPath"
    }
    $aabItem = Get-Item -LiteralPath $AabPath

    if (-not $ExistingVersionCode) {
        $ExistingVersionCode = $defaultVersionCode
    }
}

$uploadStoreIcon = $false
if (-not $SkipStoreIcon) {
    if (-not $StoreIconPath) {
        $StoreIconPath = $DefaultStoreIconPath
    }

    $StoreIconPath = Resolve-InputPath $StoreIconPath
    if (Test-Path -LiteralPath $StoreIconPath) {
        $uploadStoreIcon = $true
        $storeIconItem = Get-Item -LiteralPath $StoreIconPath
    }
    else {
        Write-Warning "No existe el icono de ficha de Play Console: $StoreIconPath. Se continuara sin subir icono."
        $StoreIconPath = $null
    }
}

$updateStoreListing = $false
if (-not $SkipStoreListing) {
    $storeListingConfig = Get-StoreListingConfig $StoreListingPath
    $updateStoreListing = $true

    if (-not $PSBoundParameters.ContainsKey('StoreListingLanguage') -and -not [string]::IsNullOrWhiteSpace($storeListingConfig.Language)) {
        $StoreListingLanguage = $storeListingConfig.Language
    }
    if (-not $PSBoundParameters.ContainsKey('StoreTitle')) {
        $StoreTitle = $storeListingConfig.Title
    }
    if (-not $PSBoundParameters.ContainsKey('StoreShortDescription')) {
        $StoreShortDescription = $storeListingConfig.ShortDescription
    }
    if (-not $PSBoundParameters.ContainsKey('StoreFullDescription')) {
        $StoreFullDescription = $storeListingConfig.FullDescription
    }

    Test-StoreListingPolicy `
        -Title $StoreTitle `
        -ShortDescription $StoreShortDescription `
        -FullDescription $StoreFullDescription
}

Write-Section 'Autenticacion'
if (-not $ServiceAccountJson) {
    $ServiceAccountJson = $DefaultServiceAccountJson
}

$ServiceAccountJson = Resolve-InputPath $ServiceAccountJson
if (-not (Test-Path -LiteralPath $ServiceAccountJson)) {
    throw "No existe el JSON de cuenta de servicio: $ServiceAccountJson"
}

$resolvedJsonPath = (Resolve-Path -LiteralPath $ServiceAccountJson).Path
$resolvedProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
if ($resolvedJsonPath.StartsWith($resolvedProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning 'El JSON de cuenta de servicio esta dentro del repo. No lo subas a git.'
}

Write-Host "Usando cuenta de servicio: $ServiceAccountJson"
Write-Host 'Obteniendo access token OAuth desde cuenta de servicio...'
$AccessToken = Get-AccessTokenFromServiceAccount $ServiceAccountJson

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    throw 'Access token requerido.'
}

Write-Section 'Release'
if (-not $Track) {
    Write-Host 'Tracks comunes: internal, alpha, beta, production.'
    Write-Host 'Para closed testing personalizado, escribe el nombre exacto del track.'
    $Track = Read-Default 'Track' 'internal'
}
if ([string]::IsNullOrWhiteSpace($Track)) {
    throw 'Track requerido.'
}

if ($PSBoundParameters.ContainsKey('Status')) {
    $Status = Normalize-Status $Status
}
else {
    $Status = Read-Status $Status
}
if (-not $ReleaseName) {
    $ReleaseName = Read-Default 'Nombre del release' $defaultReleaseName
}

$userFraction = $null
if ($Status -eq 'inProgress') {
    $userFraction = Read-PercentAsFraction 'Porcentaje de rollout' 5
}

if (-not $PSBoundParameters.ContainsKey('ReleaseNotes')) {
    if (Read-YesNo 'Anadir notas de version?' $false) {
        $ReleaseNotesLanguage = Read-Default 'Idioma notas release' $ReleaseNotesLanguage
        Write-Host 'Escribe las notas. Linea vacia para terminar.'
        $noteLines = @()
        while ($true) {
            $line = Read-Host '>'
            if ([string]::IsNullOrEmpty($line)) {
                break
            }
            $noteLines += $line
        }
        $ReleaseNotes = $noteLines -join "`n"
    }
}

if (-not $PSBoundParameters.ContainsKey('ValidateOnly')) {
    $ValidateOnly = Read-YesNo 'Validar sin hacer commit?' $false
}

$shouldSendForReview = $false
$shouldErrorIfInReview = $true
if (-not $ValidateOnly) {
    if ($PSBoundParameters.ContainsKey('ErrorIfInReview')) {
        $shouldErrorIfInReview = $ErrorIfInReview
    }
    else {
        $shouldErrorIfInReview = Read-YesNo 'Si ya hay cambios en review, fallar en vez de cancelarlos?' $true
    }

    if ($Status -ne 'draft') {
        if ($PSBoundParameters.ContainsKey('SendForReview')) {
            $shouldSendForReview = $SendForReview
        }
        else {
            $shouldSendForReview = Read-YesNo 'Enviar a review/publicacion al hacer commit?' $true
        }
    }
}

Write-Section 'Resumen'
Write-Host "Package: $PackageName"
if ($SkipAabUpload) {
    Write-Host "AAB: omitido; se reutilizara VersionCode $ExistingVersionCode"
}
else {
    Write-Host "AAB: $AabPath"
    Write-Host "Tamano AAB: $($aabItem.Length) bytes"
    if ($ReuseExistingVersionCode -and -not [string]::IsNullOrWhiteSpace($ExistingVersionCode)) {
        Write-Host "Si Play rechaza el AAB por version repetida, se reutilizara VersionCode $ExistingVersionCode"
    }
}
Write-Host "Track: $Track"
Write-Host "Estado: $Status"
Write-Host "Release: $ReleaseName"
if ($uploadStoreIcon) {
    Write-Host "Icono Play: $StoreIconPath"
    Write-Host "Idioma ficha: $StoreListingLanguage"
    Write-Host "Tamano icono: $($storeIconItem.Length) bytes"
}
if ($updateStoreListing) {
    Write-Host "Ficha Play: $StoreListingLanguage"
    Write-Host "Titulo ficha: $StoreTitle"
    Write-Host "Descripcion corta: $StoreShortDescription"
    if ($storeListingConfig.Path) {
        Write-Host "Metadata ficha: $($storeListingConfig.Path)"
    }
}
if ($null -ne $userFraction) {
    Write-Host "Rollout: $([Math]::Round($userFraction * 100, 4))%"
}
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    Write-Host "Notas: $ReleaseNotesLanguage"
}
Write-Host "Modo: $(if ($ValidateOnly) { 'VALIDAR SIN COMMIT' } else { 'SUBIR Y COMMIT' })"
if (-not $ValidateOnly -and $Status -ne 'draft') {
    Write-Host "Enviar a review/publicacion: $shouldSendForReview"
}

$confirmationWord = if ($ValidateOnly) {
    'VALIDAR'
}
elseif ($Status -eq 'draft' -or -not $shouldSendForReview) {
    'SUBIR'
}
else {
    'PUBLICAR'
}

if ($AssumeYes) {
    Write-Host "Confirmacion automatica: $confirmationWord"
}
else {
    $confirmation = Read-Host "Escribe $confirmationWord para continuar"
    $confirmationUpper = $confirmation.Trim().ToUpperInvariant()
    if ($confirmationUpper -cne $confirmationWord -and (($confirmationUpper -cne 'SUBIR') -or $ValidateOnly)) {
        Write-Host 'Cancelado.'
        exit 0
    }
}

$headers = @{
    Authorization = "Bearer $AccessToken"
}

$escapedPackageName = Escape-PathSegment $PackageName
$escapedTrack = Escape-PathSegment $Track
$editId = $null

try {
    Write-Section 'Creando edit'
    $edit = Invoke-GoogleJsonApi `
        -Method POST `
        -Uri "$ApiRoot/applications/$escapedPackageName/edits" `
        -Headers $headers `
        -Body ([ordered]@{})
    $editId = $edit.id
    if ([string]::IsNullOrWhiteSpace($editId)) {
        throw 'Google no devolvio edit id.'
    }
    Write-Host "Edit creado: $editId"

    if ($updateStoreListing) {
        Write-Section 'Actualizando ficha Play Console'
        $escapedStoreListingLanguage = Escape-PathSegment $StoreListingLanguage
        $listingBody = [ordered]@{
            language = $StoreListingLanguage
            title = $StoreTitle
            shortDescription = $StoreShortDescription
            fullDescription = $StoreFullDescription
        }
        Invoke-GoogleJsonApi `
            -Method PUT `
            -Uri "$ApiRoot/applications/$escapedPackageName/edits/$editId/listings/$escapedStoreListingLanguage" `
            -Headers $headers `
            -Body $listingBody | Out-Null
        Write-Host 'Ficha actualizada.'
    }

    if ($SkipAabUpload) {
        $versionCode = [string]$ExistingVersionCode
        Write-Section 'Reutilizando version existente'
        Write-Host "VersionCode: $versionCode"
    }
    else {
        Write-Section 'Subiendo AAB'
        try {
            $bundle = Invoke-GoogleMediaUpload `
                -Uri (Add-QueryString "$UploadRoot/applications/$escapedPackageName/edits/$editId/bundles" @{ uploadType = 'media' }) `
                -Headers $headers `
                -FilePath $AabPath
            $versionCode = [string]$bundle.versionCode
        }
        catch {
            $bundleUploadError = Get-GoogleApiErrorText $_
            if ($ReuseExistingVersionCode -and $bundleUploadError -match 'Version code .+ has already been used' -and -not [string]::IsNullOrWhiteSpace($ExistingVersionCode)) {
                Write-Warning $bundleUploadError
                $versionCode = [string]$ExistingVersionCode
                Write-Host "Se reutilizara VersionCode existente: $versionCode"
            }
            else {
                throw $bundleUploadError
            }
        }

        if ([string]::IsNullOrWhiteSpace($versionCode)) {
            throw 'Google no devolvio versionCode del bundle.'
        }

        if ($versionCode -eq [string]$ExistingVersionCode -and $ReuseExistingVersionCode) {
            Write-Host "VersionCode listo: $versionCode"
        }
        else {
            Write-Host "Bundle subido. VersionCode: $versionCode"
        }
    }

    if ($uploadStoreIcon) {
        Write-Section 'Subiendo icono Play Console'
        $escapedStoreListingLanguage = Escape-PathSegment $StoreListingLanguage
        $iconResult = Invoke-GoogleMediaUpload `
            -Uri (Add-QueryString "$UploadRoot/applications/$escapedPackageName/edits/$editId/listings/$escapedStoreListingLanguage/icon" @{ uploadType = 'media' }) `
            -Headers $headers `
            -FilePath $StoreIconPath `
            -ContentType 'image/png'

        if ($iconResult.image.id) {
            Write-Host "Icono subido. Id: $($iconResult.image.id)"
        }
        else {
            Write-Host 'Icono subido.'
        }
    }

    Write-Section 'Actualizando track'
    $release = [ordered]@{
        name = $ReleaseName
        versionCodes = @($versionCode)
        status = $Status
    }
    if ($null -ne $userFraction) {
        $release.userFraction = $userFraction
    }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $release.releaseNotes = @(
            [ordered]@{
                language = $ReleaseNotesLanguage
                text = $ReleaseNotes
            }
        )
    }

    $trackBody = [ordered]@{
        track = $Track
        releases = @($release)
    }
    $trackResult = Invoke-GoogleJsonApi `
        -Method PUT `
        -Uri "$ApiRoot/applications/$escapedPackageName/edits/$editId/tracks/$escapedTrack" `
        -Headers $headers `
        -Body $trackBody
    Write-Host "Track actualizado: $($trackResult.track)"

    if ($ValidateOnly) {
        Write-Section 'Validando edit'
        Invoke-GoogleJsonApi `
            -Method POST `
            -Uri "$ApiRoot/applications/$escapedPackageName/edits/$($editId):validate" `
            -Headers $headers | Out-Null
        Write-Host 'Edit validado correctamente.'

        Write-Section 'Eliminando edit temporal'
        Invoke-GoogleJsonApi `
            -Method DELETE `
            -Uri "$ApiRoot/applications/$escapedPackageName/edits/$editId" `
            -Headers $headers | Out-Null
        Write-Host 'Edit temporal eliminado.'
    }
    else {
        Write-Section 'Commit'
        $commitQuery = @{
            changesInReviewBehavior = if ($shouldErrorIfInReview) { 'ERROR_IF_IN_REVIEW' } else { 'CANCEL_IN_REVIEW_AND_SUBMIT' }
        }
        if (-not $shouldSendForReview -and $Status -ne 'draft') {
            $commitQuery.changesNotSentForReview = 'true'
        }
        $commitUri = Add-QueryString "$ApiRoot/applications/$escapedPackageName/edits/$($editId):commit" $commitQuery
        Invoke-GoogleJsonApi `
            -Method POST `
            -Uri $commitUri `
            -Headers $headers | Out-Null
        Write-Host 'Commit completado.'

        Write-Section 'Resultado'
        if ($Status -eq 'draft') {
            Write-Host 'Release creado como draft en Play Console.'
        }
        elseif ($shouldSendForReview) {
            Write-Host 'Cambios enviados a review/publicacion segun el flujo de Play Console.'
        }
        else {
            Write-Host 'Cambios subidos y dejados sin enviar a review. Revisa Play Console.'
        }
    }

    Write-Host
    Write-Host 'Proceso completado.'
}
catch {
    Write-Host
    Write-Host "ERROR: $(Get-GoogleApiErrorText $_)"
    if (-not [string]::IsNullOrWhiteSpace($editId)) {
        try {
            Write-Host "Intentando eliminar edit no confirmado: $editId"
            Invoke-GoogleJsonApi `
                -Method DELETE `
                -Uri "$ApiRoot/applications/$escapedPackageName/edits/$editId" `
                -Headers $headers | Out-Null
        }
        catch {
            Write-Host "No se pudo eliminar el edit: $(Get-GoogleApiErrorText $_)"
        }
    }
    exit 1
}
