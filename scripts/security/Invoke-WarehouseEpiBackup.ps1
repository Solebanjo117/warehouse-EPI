[CmdletBinding()]
param(
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$PgDumpPath = 'C:\Program Files\PostgreSQL\18\bin\pg_dump.exe',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseName = 'warehouseEPI',
    [string]$DatabaseUser = 'postgres',
    [ValidateRange(1, 365)]
    [int]$RetentionDays = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ProgramDataPath([string]$Path, [string]$Description) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith('C:\ProgramData\WarehouseEPI\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description debe permanecer dentro de C:\ProgramData\WarehouseEPI."
    }
    return $resolved
}

$backupPath = Assert-ProgramDataPath $BackupDirectory 'El directorio de respaldos'
$passPath = Assert-ProgramDataPath $PgPassFile 'El archivo de credenciales'
if (-not (Test-Path -LiteralPath $backupPath -PathType Container)) { throw 'El directorio de respaldos no existe.' }
if (-not (Test-Path -LiteralPath $passPath -PathType Leaf)) { throw 'El archivo de credenciales no existe.' }
if (-not (Test-Path -LiteralPath $PgDumpPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PgRestorePath -PathType Leaf)) {
    throw 'No se encontraron las herramientas PostgreSQL requeridas para el respaldo.'
}

$env:PGPASSFILE = $passPath
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$finalPath = Join-Path $backupPath "warehouseEPI-$timestamp.dump"
$temporaryPath = "$finalPath.partial"
try {
    & $PgDumpPath --format=custom --no-owner --no-privileges --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --file=$temporaryPath $DatabaseName 2>$null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
        throw 'La creación del respaldo PostgreSQL falló.'
    }

    & $PgRestorePath --list $temporaryPath 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La validación del respaldo PostgreSQL falló.' }

    Move-Item -LiteralPath $temporaryPath -Destination $finalPath -ErrorAction Stop
    $cutoff = [DateTimeOffset]::UtcNow.AddDays(-$RetentionDays)
    $expiredBackups = Get-ChildItem -LiteralPath $backupPath -Filter 'warehouseEPI-*.dump' -File |
        Where-Object { $_.LastWriteTimeUtc -lt $cutoff.UtcDateTime }
    foreach ($expiredBackup in $expiredBackups) {
        Remove-Item -LiteralPath $expiredBackup.FullName -Force
    }
    Write-Host "Respaldo validado: $(Split-Path -Leaf $finalPath)"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
}
