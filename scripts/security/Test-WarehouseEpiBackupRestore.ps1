[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupPath,
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
$resolvedPassFile = [IO.Path]::GetFullPath($PgPassFile)
if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Leaf)) { throw 'El respaldo indicado no existe.' }
if (-not (Test-Path -LiteralPath $resolvedPassFile -PathType Leaf)) { throw 'El archivo de credenciales no existe.' }
if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PgRestorePath -PathType Leaf)) {
    throw 'No se encontraron las herramientas PostgreSQL requeridas para la restauración.'
}

$temporaryDatabase = "warehouse_epi_restore_validation_$([Guid]::NewGuid().ToString('N'))"
$env:PGPASSFILE = $resolvedPassFile
$created = $false
try {
    & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=postgres --set=ON_ERROR_STOP=1 --command="CREATE DATABASE $temporaryDatabase" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible crear la base temporal de validación.' }
    $created = $true

    & $PgRestorePath --no-owner --no-privileges --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase $resolvedBackup 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La restauración de validación falló.' }

    $tableCount = & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=$temporaryDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'" 2>$null
    if ($LASTEXITCODE -ne 0 -or [int]$tableCount -lt 1) { throw 'La base restaurada no contiene el esquema esperado.' }
    Write-Host 'La restauración aislada fue validada correctamente.'
}
finally {
    if ($created) {
        & $PsqlPath --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --dbname=postgres --set=ON_ERROR_STOP=1 --command="DROP DATABASE IF EXISTS $temporaryDatabase WITH (FORCE)" 2>$null | Out-Null
    }
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
}
