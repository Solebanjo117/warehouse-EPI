[CmdletBinding()]
param(
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseUser = 'postgres'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$latestBackup = Get-ChildItem -LiteralPath $BackupDirectory -Filter 'warehouseEPI-*.dump' -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $latestBackup) { throw 'No existe un respaldo validado para comprobar la recuperación.' }
$referenceBackup = Join-Path $BackupDirectory ($latestBackup.BaseName + '-references.zip')

& (Join-Path $PSScriptRoot 'Test-WarehouseEpiBackupRestore.ps1') -BackupPath $latestBackup.FullName `
    -ReferenceBackupPath $referenceBackup `
    -PgPassFile $PgPassFile -PsqlPath $PsqlPath -PgRestorePath $PgRestorePath `
    -DatabaseHost $DatabaseHost -DatabasePort $DatabasePort -DatabaseUser $DatabaseUser
if ($LASTEXITCODE -ne 0) { throw 'La validación semanal de recuperación falló.' }
