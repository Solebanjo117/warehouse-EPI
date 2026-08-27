[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupPath,
    [string]$ReferenceBackupPath,
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseUser = 'postgres'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
$resolvedReferenceBackup = if ([string]::IsNullOrWhiteSpace($ReferenceBackupPath)) {
    Join-Path (Split-Path -Parent $resolvedBackup) ([IO.Path]::GetFileNameWithoutExtension($resolvedBackup) + '-references.zip')
} else { [IO.Path]::GetFullPath($ReferenceBackupPath) }
$resolvedPassFile = [IO.Path]::GetFullPath($PgPassFile)
if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Leaf)) { throw 'El respaldo indicado no existe.' }
if (-not (Test-Path -LiteralPath $resolvedPassFile -PathType Leaf)) { throw 'El archivo de credenciales no existe.' }
if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PgRestorePath -PathType Leaf)) {
    throw 'No se encontraron las herramientas PostgreSQL requeridas para la restauración.'
}

$temporaryDatabase = "warehouse_epi_restore_validation_$([Guid]::NewGuid().ToString('N'))"
$env:PGPASSFILE = $resolvedPassFile
$created = $false
$referenceValidationPath = Join-Path ([IO.Path]::GetTempPath()) "warehouse-epi-reference-restore-$([Guid]::NewGuid().ToString('N'))"
try {
    & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=postgres --set=ON_ERROR_STOP=1 --command="CREATE DATABASE $temporaryDatabase" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible crear la base temporal de validación.' }
    $created = $true

    & $PgRestorePath --no-owner --no-privileges --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase $resolvedBackup 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La restauración de validación falló.' }

    $tableCount = & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'" 2>$null
    if ($LASTEXITCODE -ne 0 -or [int]$tableCount -lt 1) { throw 'La base restaurada no contiene el esquema esperado.' }
    $referenceTable = & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT to_regclass('public.warehouse_map_reference_images') IS NOT NULL" 2>$null
    $referenceCount = 0
    if ($referenceTable.Trim() -eq 't') {
        $referenceCount = [int](& $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM warehouse_map_reference_images" 2>$null)
    }
    if ($referenceCount -gt 0 -and -not (Test-Path -LiteralPath $resolvedReferenceBackup -PathType Leaf)) {
        throw 'La base contiene fondos de referencia, pero falta su respaldo de archivos asociado.'
    }
    if (Test-Path -LiteralPath $resolvedReferenceBackup -PathType Leaf) {
        New-Item -ItemType Directory -Path $referenceValidationPath | Out-Null
        Expand-Archive -LiteralPath $resolvedReferenceBackup -DestinationPath $referenceValidationPath
        $manifestPath = Join-Path $referenceValidationPath 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'El respaldo de referencias no contiene manifiesto.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($entry in $manifest.Files) {
            if ([IO.Path]::GetFileName($entry.Name) -ne $entry.Name) { throw 'El manifiesto de referencias contiene una ruta insegura.' }
            $restoredFile = Join-Path $referenceValidationPath $entry.Name
            if (-not (Test-Path -LiteralPath $restoredFile -PathType Leaf)) { throw "Falta el archivo de referencia '$($entry.Name)'." }
            $hash = (Get-FileHash -LiteralPath $restoredFile -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $entry.Sha256 -or (Get-Item -LiteralPath $restoredFile).Length -ne $entry.Length) { throw "El archivo de referencia '$($entry.Name)' no coincide con su manifiesto." }
        }
    }
    Write-Host 'La restauración aislada fue validada correctamente.'
}
finally {
    if ($created) {
        & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=postgres --set=ON_ERROR_STOP=1 --command="DROP DATABASE IF EXISTS $temporaryDatabase WITH (FORCE)" 2>$null | Out-Null
    }
    if (Test-Path -LiteralPath $referenceValidationPath) { Remove-Item -LiteralPath $referenceValidationPath -Recurse -Force }
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
}
