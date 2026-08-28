[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [string]$ReferenceDirectory = 'C:\ProgramData\WarehouseEPI\WarehouseMapReferences',
    [string]$BrandingDirectory = 'C:\ProgramData\WarehouseEPI\Branding',
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseUser = 'postgres'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-EmptyOrMissingDirectory([string]$Path, [string]$Description) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolved -PathType Leaf) { throw "$Description no puede ser un archivo." }
    if ((Test-Path -LiteralPath $resolved -PathType Container) -and
        (Get-ChildItem -LiteralPath $resolved -Force | Select-Object -First 1)) {
        throw "$Description debe estar vacío antes de restaurar."
    }
    return $resolved
}

$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
$resolvedPassFile = [IO.Path]::GetFullPath($PgPassFile)
$resolvedReferences = Assert-EmptyOrMissingDirectory $ReferenceDirectory 'El directorio de referencias'
$resolvedBranding = Assert-EmptyOrMissingDirectory $BrandingDirectory 'El directorio de branding'
if (-not (Test-Path -LiteralPath $resolvedPassFile -PathType Leaf)) { throw 'El archivo protegido PGPASSFILE no existe.' }
if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PgRestorePath -PathType Leaf)) {
    throw 'No se encontraron las herramientas PostgreSQL 18 requeridas.'
}

$manifest = & (Join-Path $PSScriptRoot 'Test-WarehouseEpiMigrationBackup.ps1') `
    -PackagePath $resolvedPackage -RequireExternalHash
$databaseExists = $null
$env:PGPASSFILE = $resolvedPassFile
try {
    $databaseExists = & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser `
        --dbname=postgres --tuples-only --no-align --set=ON_ERROR_STOP=1 `
        --command="SELECT 1 FROM pg_database WHERE datname = 'warehouseEPI'" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible consultar el destino PostgreSQL.' }
}
finally { Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue }
if ([string]$databaseExists -eq '1') {
    throw 'warehouseEPI ya existe. Este script nunca reemplaza ni elimina una base existente.'
}

if (-not $PSCmdlet.ShouldProcess(
    "PostgreSQL en ${DatabaseHost}:$DatabasePort y directorios C:\ProgramData\WarehouseEPI",
    'Crear warehouseEPI y restaurar el paquete de migración')) { return }

$stagingPath = Join-Path ([IO.Path]::GetTempPath()) "warehouse-epi-restore-$([Guid]::NewGuid().ToString('N'))"
$databaseCreated = $false
$copiedFiles = [Collections.Generic.List[string]]::new()
try {
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $stagingPath
    $databaseRelativePath = [string](@($manifest.Files | Where-Object { $_.Kind -ceq 'database' })[0].Path)
    $referencesRelativePath = [string](@($manifest.Files | Where-Object { $_.Kind -ceq 'references' })[0].Path)
    $databaseBackup = Join-Path $stagingPath $databaseRelativePath.Replace('/', '\')
    $referenceBackup = Join-Path $stagingPath $referencesRelativePath.Replace('/', '\')

    $env:PGPASSFILE = $resolvedPassFile
    & (Join-Path $PSScriptRoot 'Test-WarehouseEpiBackupRestore.ps1') `
        -BackupPath $databaseBackup -ReferenceBackupPath $referenceBackup `
        -PgPassFile $resolvedPassFile -PsqlPath $PsqlPath -PgRestorePath $PgRestorePath `
        -DatabaseHost $DatabaseHost -DatabasePort $DatabasePort -DatabaseUser $DatabaseUser
    if ($LASTEXITCODE -ne 0) { throw 'La validación aislada del paquete falló.' }

    $env:PGPASSFILE = $resolvedPassFile
    & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser `
        --dbname=postgres --set=ON_ERROR_STOP=1 --command='CREATE DATABASE "warehouseEPI"' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible crear warehouseEPI.' }
    $databaseCreated = $true

    & $PgRestorePath --no-owner --no-privileges --exit-on-error `
        --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser `
        --dbname=warehouseEPI $databaseBackup 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La restauración de warehouseEPI falló.' }

    $tableCount = & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser `
        --dbname=warehouseEPI --tuples-only --no-align --set=ON_ERROR_STOP=1 `
        --command="SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'" 2>$null
    if ($LASTEXITCODE -ne 0 -or [int]$tableCount -lt 1) { throw 'La base restaurada no contiene tablas operativas.' }

    $referenceStaging = Join-Path $stagingPath '.references'
    Expand-Archive -LiteralPath $referenceBackup -DestinationPath $referenceStaging
    $referenceManifest = Get-Content -LiteralPath (Join-Path $referenceStaging 'manifest.json') -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path $resolvedReferences | Out-Null
    foreach ($entry in $referenceManifest.Files) {
        $source = Join-Path $referenceStaging ([string]$entry.Name)
        $destination = Join-Path $resolvedReferences ([string]$entry.Name)
        Copy-Item -LiteralPath $source -Destination $destination
        $copiedFiles.Add($destination)
    }

    New-Item -ItemType Directory -Force -Path $resolvedBranding | Out-Null
    foreach ($branding in @($manifest.Files | Where-Object { $_.Kind -ceq 'branding' })) {
        $source = Join-Path $stagingPath ([string]$branding.Path).Replace('/', '\')
        $destination = Join-Path $resolvedBranding (Split-Path -Leaf ([string]$branding.Path))
        Copy-Item -LiteralPath $source -Destination $destination
        $copiedFiles.Add($destination)
    }

    Write-Host 'warehouseEPI, las referencias y el branding fueron restaurados correctamente.'
    Write-Host 'Falta configurar Security:PinLookupKey, certificado, rol mínimo y servicio en esta laptop.'
    $databaseCreated = $false
}
catch {
    foreach ($copiedFile in $copiedFiles) {
        if (Test-Path -LiteralPath $copiedFile -PathType Leaf) { Remove-Item -LiteralPath $copiedFile -Force }
    }
    if ($databaseCreated) {
        & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser `
            --dbname=postgres --set=ON_ERROR_STOP=1 `
            --command='DROP DATABASE IF EXISTS "warehouseEPI" WITH (FORCE)' 2>$null | Out-Null
    }
    throw
}
finally {
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stagingPath) { Remove-Item -LiteralPath $stagingPath -Recurse -Force }
}
