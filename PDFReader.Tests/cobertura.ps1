# Pruebas con cobertura (constitucion General 8.6). Uso, desde cualquier carpeta:
#   pwsh PDFReader.Tests/cobertura.ps1
# Compila, pasa las pruebas con coverlet, resume con ReportGenerator (dotnet tool local del repo)
# y da las dos cifras: cobertura de lo instrumentado y cobertura sobre toda la app.
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

# Lineas cubiertas (sin repetir fichero+linea) frente a las lineas de codigo C# de toda la app:
# sin obj/bin, pruebas, constitucion, generados (*.g.cs, *.Designer.cs); no vacias ni comentarios.
[xml]$cov = Get-Content $xml.FullName
$covered = @{}
foreach ($class in $cov.coverage.packages.package.classes.class) {
    foreach ($line in $class.lines.line) {
        if ([int]$line.hits -gt 0) { $covered["$($class.filename)|$($line.number)"] = 1 }
    }
}
$total = 0
Get-ChildItem $repo -Recurse -Filter *.cs | Where-Object {
    $_.FullName -notmatch '\\(obj|bin|constitution|[^\\]+\.Tests)\\' -and
    $_.Name -notmatch '\.(g|Designer)\.cs$'
} | ForEach-Object {
    $inBlock = $false
    foreach ($l in Get-Content $_.FullName) {
        $t = $l.Trim()
        if ($inBlock) { if ($t -match '\*/') { $inBlock = $false }; continue }
        if ($t -eq '' -or $t.StartsWith('//')) { continue }
        if ($t.StartsWith('/*')) { if ($t -notmatch '\*/') { $inBlock = $true }; continue }
        $total++
    }
}
"Cobertura sobre toda la app: {0} de {1} lineas = {2:N1} %" -f $covered.Count, $total, (100.0 * $covered.Count / $total)
exit $code
