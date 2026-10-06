[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
try {
    if ([string]::IsNullOrWhiteSpace($env:WAREHOUSE_EPI_TEST_CONNECTION)) {
        throw 'Configure WAREHOUSE_EPI_TEST_CONNECTION explícitamente para warehouse_epi_test.'
    }
    $connection.set_ConnectionString($env:WAREHOUSE_EPI_TEST_CONNECTION)
    if ([string]$connection['Database'] -cne 'warehouse_epi_test') {
        throw 'La conexión de rendimiento debe apuntar a warehouse_epi_test, nunca a producción.'
    }
}
finally { $connection.Clear() }

$results = Join-Path $repositoryRoot ('artifacts/test-results/performance-' + [Guid]::NewGuid().ToString('N'))
Push-Location $repositoryRoot
try {
    Write-Host 'Rendimiento aislado: requiere compilación Release previa y permiso para crear bases temporales.'
    Write-Host "Resultados: $results"
    # Sin cobertura: el recolector alteraría la medición de tiempos.
    dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --configuration Release --no-build --no-restore --filter 'Category=PostgreSQLPerformance' --logger 'trx;LogFileName=performance.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Falló la medición de rendimiento: $LASTEXITCODE." }
    $trxPath = Join-Path $results 'performance.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw 'No se generó el TRX de rendimiento.' }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -lt 1 -or [int]$counters.passed -ne [int]$counters.total) {
        throw 'La medición no ejecutó y aprobó todas las pruebas seleccionadas.'
    }
}
finally { Pop-Location }
