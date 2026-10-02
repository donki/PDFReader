# Pruebas con cobertura (constitucion General 8.6). Uso, desde cualquier carpeta:
#   pwsh PDFReader.Tests/cobertura.ps1
# Compila, pasa las pruebas con coverlet, resume con ReportGenerator (dotnet tool local del repo)
# y da las dos cifras: cobertura de lo instrumentado y cobertura sobre toda la app.
#
# Cobertura sobre toda la app (desde el 2026-10-01): por cada fichero C# de la app (sin obj/bin,
# pruebas, constitucion ni generados *.g.cs / *.Designer.cs):
#   - si el banco lo compila, cuentan sus lineas ejecutables segun coverlet (cubiertas y totales),
#     mas las de sus bloques #if ANDROID / #if WINDOWS, que el banco no compila, como no cubiertas;
#   - si no (codigo solo de Android, como MainActivity), cuentan todas sus lineas ejecutables
#     aproximadas (no vacias, ni comentarios, ni directivas #, ni solo llaves, using, namespace o
#     atributos, tambien los de varias lineas; un fichero solo de interfaces cuenta 0) y ninguna
#     como cubierta.
# Antes se contaban tambien las llaves, los using y las firmas como lineas de la app: inflaba el
# denominador con lineas que no se ejecutan nunca.
$ErrorActionPreference = 'Stop'
$tests = $PSScriptRoot
$repo = Split-Path $tests
$results = Join-Path $tests 'TestResults'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }

dotnet build $tests -m:1 -nodeReuse:false -p:UseSharedCompilation=false -v:q -nologo | Out-Host
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$watch = [Diagnostics.Stopwatch]::StartNew()
dotnet test $tests --no-build --collect:"XPlat Code Coverage" --results-directory $results -nologo | Out-Host
$code = $LASTEXITCODE
$watch.Stop()
"Tiempo del banco de pruebas (dotnet test --no-build): {0:N1} s" -f $watch.Elapsed.TotalSeconds

$xml = Get-ChildItem $results -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
Push-Location $repo
try {
    dotnet tool restore | Out-Null
    dotnet reportgenerator "-reports:$($xml.FullName)" "-targetdir:$results/report" -reporttypes:TextSummary | Out-Null
} finally { Pop-Location }
Get-Content "$results/report/Summary.txt" | Select-Object -First 12

# Lineas de coverlet por fichero (sin repetir numero de linea).
[xml]$cov = Get-Content $xml.FullName
$sources = @($cov.coverage.sources.source)
$byFile = @{}
foreach ($class in $cov.coverage.packages.package.classes.class) {
    $path = $class.filename
    if (-not [IO.Path]::IsPathRooted($path)) {
        foreach ($s in $sources) { $p = Join-Path $s $path; if (Test-Path $p) { $path = $p; break } }
    }
    $path = [IO.Path]::GetFullPath($path).ToLowerInvariant()
    if (-not $byFile.ContainsKey($path)) { $byFile[$path] = @{} }
    foreach ($line in $class.lines.line) {
        $n = [int]$line.number
        $hit = [int]$line.hits -gt 0
        if (-not $byFile[$path].ContainsKey($n) -or $hit) { $byFile[$path][$n] = $hit }
    }
}

function Get-ExecutableLines([string]$file) {
    # Un fichero solo de interfaces o enumeraciones no tiene nada que ejecutar.
    $text = Get-Content $file -Raw
    if ($text -match '\binterface\b|\benum\b' -and $text -notmatch '\b(class|record|struct)\b') { return 0 }
    $n = 0; $inBlock = $false; $attrDepth = 0
    foreach ($l in Get-Content $file) {
        $t = $l.Trim()
        if ($inBlock) { if ($t -match '\*/') { $inBlock = $false }; continue }
        # Atributos de varias lineas ([Activity(...)], [IntentFilter(...)]): no se ejecutan.
        if ($attrDepth -gt 0 -or ($t.StartsWith('[') -and -not $t.EndsWith(']'))) {
            $attrDepth += ($t.ToCharArray() | Where-Object { $_ -eq '[' }).Count - ($t.ToCharArray() | Where-Object { $_ -eq ']' }).Count
            if ($attrDepth -lt 0) { $attrDepth = 0 }
            continue
        }
        if ($t.StartsWith('/*')) { if ($t -notmatch '\*/') { $inBlock = $true }; continue }
        if ($t -eq '' -or $t.StartsWith('//') -or $t.StartsWith('#')) { continue }
        if ($t -in '{', '}', '};', '});', ')', ');', '{ }', '{}') { continue }
        if ($t -match '^(global )?using [\w.=\s]+;$' -or $t.StartsWith('namespace ')) { continue }
        if ($t.StartsWith('[') -and $t.EndsWith(']')) { continue }
        $n++
    }
    $n
}

# Lineas de codigo que solo se compilan para una plataforma (#if ANDROID / #if WINDOWS ... hasta el
# #else, #elif o #endif): el banco compila para net10.0 y coverlet no las ve, pero son de la app y
# no estan probadas, asi que cuentan como no cubiertas.
function Get-PlatformOnlyLines([string]$file) {
    $n = 0; $depth = 0; $platformDepth = 0
    foreach ($l in Get-Content $file) {
        $t = $l.Trim()
        if ($t -match '^#if\b') {
            $depth++
            if ($platformDepth -eq 0 -and $t -match '^#if\s+(ANDROID|WINDOWS|IOS|MACCATALYST)\b') { $platformDepth = $depth }
            continue
        }
        if ($t -match '^#elif\b') {
            if ($platformDepth -eq $depth) { $platformDepth = 0 }
            if ($platformDepth -eq 0 -and $t -match '^#elif\s+(ANDROID|WINDOWS|IOS|MACCATALYST)\b') { $platformDepth = $depth }
            continue
        }
        if ($t -match '^#else\b') { if ($platformDepth -eq $depth) { $platformDepth = 0 }; continue }
        if ($t -match '^#endif\b') { if ($platformDepth -eq $depth) { $platformDepth = 0 }; $depth--; continue }
        if ($platformDepth -eq 0) { continue }
        if ($t -eq '' -or $t.StartsWith('//') -or $t.StartsWith('#')) { continue }
        if ($t -in '{', '}', '};', '});', ')', ');', '{ }', '{}') { continue }
        $n++
    }
    $n
}

$covered = 0; $total = 0; $rows = @()
Get-ChildItem $repo -Recurse -Filter *.cs | Where-Object {
    $_.FullName -notmatch '\\(obj|bin|constitution|[^\\]+\.Tests)\\' -and
    $_.Name -notmatch '\.(g|g\.i|Designer)\.cs$'
} | ForEach-Object {
    $key = $_.FullName.ToLowerInvariant()
    if ($byFile.ContainsKey($key)) {
        $lines = $byFile[$key]
        $c = @($lines.Values | Where-Object { $_ }).Count; $t = $lines.Count + (Get-PlatformOnlyLines $_.FullName)
    } else {
        $c = 0; $t = Get-ExecutableLines $_.FullName
    }
    $covered += $c; $total += $t
    $rows += [pscustomobject]@{ Fichero = $_.FullName.Substring($repo.Length + 1); Cubiertas = $c; Lineas = $t; Probado = $byFile.ContainsKey($key) }
}
$rows | Where-Object { $_.Cubiertas -lt $_.Lineas } | Sort-Object { $_.Lineas - $_.Cubiertas } -Descending | Format-Table -AutoSize | Out-Host
"Cobertura sobre toda la app: {0} de {1} lineas = {2:N1} %" -f $covered, $total, (100.0 * $covered / $total)
exit $code
