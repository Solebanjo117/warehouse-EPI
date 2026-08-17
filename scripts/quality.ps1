[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Falló '$Description' con código de salida $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'WarehouseEPI.sln'
$migrationProject = Join-Path $repositoryRoot 'src/WarehouseEPI.Infrastructure/WarehouseEPI.Infrastructure.csproj'
$startupProject = Join-Path $repositoryRoot 'src/WarehouseEPI.Web/WarehouseEPI.Web.csproj'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$testResultsDirectory = Join-Path $artifactsDirectory 'test-results'
$migrationScript = Join-Path $artifactsDirectory 'migrations.sql'

Set-Location $repositoryRoot

Write-Host '==> SDK'
Invoke-NativeCommand 'dotnet --info' { dotnet --info }

Write-Host '==> Herramientas locales'
Invoke-NativeCommand 'dotnet tool restore' { dotnet tool restore }

Write-Host '==> Restauración bloqueada'
Invoke-NativeCommand 'dotnet restore --locked-mode' { dotnet restore $solution --locked-mode }

Write-Host '==> Espacios en blanco'
Invoke-NativeCommand 'git diff --check' { git diff --check }

Write-Host '==> Formato'
Invoke-NativeCommand 'dotnet format whitespace' {
    dotnet format whitespace $solution --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**'
}
Write-Host '  formato de espacios correcto'
Invoke-NativeCommand 'dotnet format style' {
    dotnet format style $solution --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**'
}
Write-Host '  formato de estilo correcto'
Invoke-NativeCommand 'dotnet format analyzers' {
    dotnet format analyzers $solution --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**'
}
Write-Host '  formato de analizadores correcto'

Write-Host '==> Compilación Release'
Invoke-NativeCommand 'dotnet build Release' { dotnet build $solution --configuration Release --no-restore }

New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
if (Test-Path -LiteralPath $testResultsDirectory) {
    Remove-Item -LiteralPath $testResultsDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $testResultsDirectory | Out-Null

Write-Host '==> Modelo de migraciones'
Invoke-NativeCommand 'dotnet ef migrations has-pending-model-changes' {
    dotnet ef migrations has-pending-model-changes --project $migrationProject --startup-project $startupProject --configuration Release --no-build
}

Write-Host '==> Script SQL idempotente'
Invoke-NativeCommand 'dotnet ef migrations script --idempotent' {
    dotnet ef migrations script --idempotent --output $migrationScript --project $migrationProject --startup-project $startupProject --configuration Release --no-build
}

Write-Host '==> Pruebas y cobertura'
Invoke-NativeCommand 'dotnet test con cobertura' {
    dotnet test $solution --configuration Release --no-build --no-restore --logger 'trx;LogFileName=test-results.trx' --results-directory $testResultsDirectory --collect:'XPlat Code Coverage'
}

# VSTest puede copiar adjuntos temporales del recolector a un subdirectorio In.
# Conservamos solo el reporte final asociado al resultado TRX.
Get-ChildItem -LiteralPath $testResultsDirectory -Directory -Recurse |
    Where-Object { $_.Name -eq 'In' } |
    Remove-Item -Recurse -Force

Write-Host '==> Umbrales de cobertura'
& (Join-Path $PSScriptRoot 'Test-Coverage.ps1') -ResultsDirectory $testResultsDirectory
