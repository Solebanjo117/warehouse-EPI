[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ResultsDirectory,

    [double]$MinimumLineRate = 0.85,

    [double]$MinimumBranchRate = 0.45
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    throw "No existe el directorio de resultados de cobertura: $ResultsDirectory"
}

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'coverage.cobertura.xml' -File -Recurse)
if ($reports.Count -ne 1) {
    throw "Se esperaba exactamente un coverage.cobertura.xml en $ResultsDirectory; se encontraron $($reports.Count)."
}

try {
    [xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
    if ($null -eq $coverage.coverage -or [string]::IsNullOrWhiteSpace($coverage.coverage.'line-rate') -or [string]::IsNullOrWhiteSpace($coverage.coverage.'branch-rate')) {
        throw 'Faltan los atributos line-rate o branch-rate en el nodo coverage.'
    }

    $lineRate = [double]$coverage.coverage.'line-rate'
    $branchRate = [double]$coverage.coverage.'branch-rate'
}
catch {
    throw "No se pudo leer el reporte Cobertura '$($reports[0].FullName)': $($_.Exception.Message)"
}

if ($lineRate -lt 0 -or $lineRate -gt 1 -or $branchRate -lt 0 -or $branchRate -gt 1) {
    throw "El reporte Cobertura contiene tasas fuera del rango permitido de 0 a 1."
}

$linePercent = $lineRate * 100
$branchPercent = $branchRate * 100
$minimumLinePercent = $MinimumLineRate * 100
$minimumBranchPercent = $MinimumBranchRate * 100

Write-Host ("Cobertura global: líneas {0:N1}% (mínimo {1:N1}%); ramas {2:N1}% (mínimo {3:N1}%)." -f $linePercent, $minimumLinePercent, $branchPercent, $minimumBranchPercent)

$failures = @()
if ($lineRate -lt $MinimumLineRate) {
    $failures += "líneas: $($linePercent.ToString('N1'))% < $($minimumLinePercent.ToString('N1'))%"
}

if ($branchRate -lt $MinimumBranchRate) {
    $failures += "ramas: $($branchPercent.ToString('N1'))% < $($minimumBranchPercent.ToString('N1'))%"
}

if ($failures.Count -gt 0) {
    throw "La puerta de cobertura no se cumplió: $($failures -join '; ')."
}
