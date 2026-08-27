[CmdletBinding()]
param(
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$ReferenceDirectory = 'C:\ProgramData\WarehouseEPI\WarehouseMapReferences',
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
$referencePath = Assert-ProgramDataPath $ReferenceDirectory 'El directorio de referencias'
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
$referenceFinalPath = Join-Path $backupPath "warehouseEPI-$timestamp-references.zip"
$referenceTemporaryPath = Join-Path $backupPath "warehouseEPI-$timestamp-references.partial.zip"
$referenceStagingPath = Join-Path $backupPath ".references-$timestamp-$([Guid]::NewGuid().ToString('N'))"
try {
    & $PgDumpPath --format=custom --no-owner --no-privileges --host=$DatabaseHost --port=$DatabasePort --username=$DatabaseUser --file=$temporaryPath $DatabaseName 2>$null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
        throw 'La creación del respaldo PostgreSQL falló.'
    }

    & $PgRestorePath --list $temporaryPath 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La validación del respaldo PostgreSQL falló.' }

    New-Item -ItemType Directory -Path $referenceStagingPath | Out-Null
    $referenceFiles = if (Test-Path -LiteralPath $referencePath -PathType Container) {
        Get-ChildItem -LiteralPath $referencePath -File | Where-Object { $_.Name -notlike '*.upload' -and $_.Name -notlike '*.json' }
    } else { @() }
    $manifestFiles = @()
    foreach ($referenceFile in $referenceFiles) {
        Copy-Item -LiteralPath $referenceFile.FullName -Destination (Join-Path $referenceStagingPath $referenceFile.Name)
        $manifestFiles += [ordered]@{ Name = $referenceFile.Name; Length = $referenceFile.Length; Sha256 = (Get-FileHash -LiteralPath $referenceFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{ SchemaVersion = 1; DatabaseBackup = (Split-Path -Leaf $finalPath); CreatedAtUtc = [DateTimeOffset]::UtcNow; Files = $manifestFiles } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $referenceStagingPath 'manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $referenceStagingPath '*') -DestinationPath $referenceTemporaryPath -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::OpenRead($referenceTemporaryPath).Dispose()
    Move-Item -LiteralPath $temporaryPath -Destination $finalPath -ErrorAction Stop
    Move-Item -LiteralPath $referenceTemporaryPath -Destination $referenceFinalPath -ErrorAction Stop
    $cutoff = [DateTimeOffset]::UtcNow.AddDays(-$RetentionDays)
    $expiredBackups = Get-ChildItem -LiteralPath $backupPath -Filter 'warehouseEPI-*.dump' -File |
        Where-Object { $_.LastWriteTimeUtc -lt $cutoff.UtcDateTime }
    foreach ($expiredBackup in $expiredBackups) {
        $expiredReference = Join-Path $backupPath ($expiredBackup.BaseName + '-references.zip')
        Remove-Item -LiteralPath $expiredBackup.FullName -Force
        if (Test-Path -LiteralPath $expiredReference -PathType Leaf) { Remove-Item -LiteralPath $expiredReference -Force }
    }
    Write-Host "Respaldos validados: $(Split-Path -Leaf $finalPath) y $(Split-Path -Leaf $referenceFinalPath)"
}
catch {
    if (Test-Path -LiteralPath $finalPath -PathType Leaf) { Remove-Item -LiteralPath $finalPath -Force }
    if (Test-Path -LiteralPath $referenceFinalPath -PathType Leaf) { Remove-Item -LiteralPath $referenceFinalPath -Force }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    if (Test-Path -LiteralPath $referenceTemporaryPath) { Remove-Item -LiteralPath $referenceTemporaryPath -Force }
    if (Test-Path -LiteralPath $referenceStagingPath) { Remove-Item -LiteralPath $referenceStagingPath -Recurse -Force }
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
}
