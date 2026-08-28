[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$ReferenceDirectory = 'C:\ProgramData\WarehouseEPI\WarehouseMapReferences',
    [string]$BrandingDirectory = 'C:\ProgramData\WarehouseEPI\Branding',
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$PgDumpPath = 'C:\Program Files\PostgreSQL\18\bin\pg_dump.exe',
    [string]$PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseUser = 'postgres'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Add-ManifestFile(
    [Collections.Generic.List[object]]$Files,
    [string]$Root,
    [string]$Path,
    [string]$Kind) {
    $item = Get-Item -LiteralPath $Path
    $relativePath = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
    $Files.Add([ordered]@{
        Path = $relativePath
        Kind = $Kind
        Length = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$resolvedDestination = [IO.Path]::GetFullPath($DestinationDirectory)
$resolvedBackupDirectory = [IO.Path]::GetFullPath($BackupDirectory)
$resolvedReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
$resolvedBrandingDirectory = [IO.Path]::GetFullPath($BrandingDirectory)
if ($resolvedDestination.Equals($resolvedBackupDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedDestination.StartsWith($resolvedBackupDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedDestination.Equals($resolvedReferenceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedDestination.StartsWith($resolvedReferenceDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedDestination.Equals($resolvedBrandingDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedDestination.StartsWith($resolvedBrandingDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El destino del paquete debe permanecer fuera de los directorios operativos respaldados.'
}
New-Item -ItemType Directory -Force -Path $resolvedDestination | Out-Null

$before = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $resolvedBackupDirectory -PathType Container) {
    Get-ChildItem -LiteralPath $resolvedBackupDirectory -Filter 'warehouseEPI-*.dump' -File |
        ForEach-Object { $null = $before.Add($_.FullName) }
}

& (Join-Path $PSScriptRoot 'Invoke-WarehouseEpiBackup.ps1') `
    -BackupDirectory $resolvedBackupDirectory -ReferenceDirectory $ReferenceDirectory `
    -PgPassFile $PgPassFile -PgDumpPath $PgDumpPath -PgRestorePath $PgRestorePath `
    -DatabaseHost $DatabaseHost -DatabasePort $DatabasePort -DatabaseUser $DatabaseUser
if ($LASTEXITCODE -ne 0) { throw 'No fue posible crear el respaldo base para la migración.' }

$newBackups = @(Get-ChildItem -LiteralPath $resolvedBackupDirectory -Filter 'warehouseEPI-*.dump' -File |
    Where-Object { -not $before.Contains($_.FullName) })
if ($newBackups.Count -ne 1) {
    throw 'No fue posible identificar de forma inequívoca el respaldo recién creado.'
}
$databaseBackup = $newBackups[0]
$referenceBackup = Join-Path $resolvedBackupDirectory ($databaseBackup.BaseName + '-references.zip')
if (-not (Test-Path -LiteralPath $referenceBackup -PathType Leaf)) {
    throw 'Falta el ZIP pareado de referencias del respaldo recién creado.'
}

& (Join-Path $PSScriptRoot 'Test-WarehouseEpiBackupRestore.ps1') `
    -BackupPath $databaseBackup.FullName -ReferenceBackupPath $referenceBackup `
    -PgPassFile $PgPassFile -PsqlPath $PsqlPath -PgRestorePath $PgRestorePath `
    -DatabaseHost $DatabaseHost -DatabasePort $DatabasePort -DatabaseUser $DatabaseUser
if ($LASTEXITCODE -ne 0) { throw 'La restauración aislada previa al empaquetado falló.' }

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$packageName = "WarehouseEPI-migration-$timestamp.zip"
$packagePath = Join-Path $resolvedDestination $packageName
$hashPath = "$packagePath.sha256"
$temporaryPackagePath = Join-Path $resolvedDestination ".$packageName.partial.zip"
$temporaryHashPath = Join-Path $resolvedDestination ".$packageName.sha256.partial"
$stagingPath = Join-Path ([IO.Path]::GetTempPath()) "warehouse-epi-migration-$([Guid]::NewGuid().ToString('N'))"
if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath $hashPath) -or
    (Test-Path -LiteralPath $temporaryPackagePath) -or (Test-Path -LiteralPath $temporaryHashPath)) {
    throw "Ya existe un paquete de migración con el nombre '$packageName'."
}

try {
    $databaseStaging = Join-Path $stagingPath 'database'
    $referencesStaging = Join-Path $stagingPath 'references'
    $brandingStaging = Join-Path $stagingPath 'branding'
    New-Item -ItemType Directory -Path $databaseStaging, $referencesStaging, $brandingStaging | Out-Null
    Copy-Item -LiteralPath $databaseBackup.FullName -Destination $databaseStaging
    Copy-Item -LiteralPath $referenceBackup -Destination $referencesStaging

    if (Test-Path -LiteralPath $resolvedBrandingDirectory -PathType Container) {
        Get-ChildItem -LiteralPath $resolvedBrandingDirectory -File |
            Where-Object { $_.Name -match '^[a-f0-9]{32}\.(png|jpg|webp)$' } |
            Copy-Item -Destination $brandingStaging
    }

    $instructionsPath = Join-Path $stagingPath 'RESTORE.txt'
    @'
Warehouse EPI - respaldo de migración

Este ZIP contiene la base PostgreSQL, referencias del croquis, branding y hashes.
No contiene contraseñas, certificados privados ni Security:PinLookupKey.

Antes de restaurar:
1. Instale PostgreSQL 18 y clone el mismo commit del repositorio.
2. Transfiera Security:PinLookupKey y la CA PFX por un canal cifrado separado.
3. Cree el archivo protegido PGPASSFILE en la laptop destino.
4. Ejecute Test-WarehouseEpiMigrationBackup.ps1.
5. Ejecute Restore-WarehouseEpiMigrationBackup.ps1; warehouseEPI no debe existir.
'@ | Set-Content -LiteralPath $instructionsPath -Encoding utf8NoBOM

    $files = [Collections.Generic.List[object]]::new()
    Add-ManifestFile $files $stagingPath (Join-Path $databaseStaging $databaseBackup.Name) 'database'
    Add-ManifestFile $files $stagingPath (Join-Path $referencesStaging (Split-Path -Leaf $referenceBackup)) 'references'
    Add-ManifestFile $files $stagingPath $instructionsPath 'instructions'
    Get-ChildItem -LiteralPath $brandingStaging -File | Sort-Object Name |
        ForEach-Object { Add-ManifestFile $files $stagingPath $_.FullName 'branding' }

    [ordered]@{
        SchemaVersion = 1
        PackageType = 'WarehouseEPI-MigrationBackup'
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        DatabaseName = 'warehouseEPI'
        ContainsSecrets = $false
        RequiredExternalSecrets = @('Security:PinLookupKey', 'PostgreSQL credentials', 'LAN CA PFX')
        Files = $files
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stagingPath 'manifest.json') -Encoding utf8NoBOM

    Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $temporaryPackagePath -CompressionLevel Optimal
    $null = & (Join-Path $PSScriptRoot 'Test-WarehouseEpiMigrationBackup.ps1') -PackagePath $temporaryPackagePath
    $packageHash = (Get-FileHash -LiteralPath $temporaryPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $temporaryHashPath -Value "$packageHash  $packageName" -Encoding ascii
    Move-Item -LiteralPath $temporaryPackagePath -Destination $packagePath
    try { Move-Item -LiteralPath $temporaryHashPath -Destination $hashPath }
    catch {
        Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
        throw
    }
    Write-Host "Paquete de migración validado: $packagePath"
    [pscustomobject]@{ PackagePath = $packagePath; Sha256Path = $hashPath }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) { Remove-Item -LiteralPath $stagingPath -Recurse -Force }
    if (Test-Path -LiteralPath $temporaryPackagePath) { Remove-Item -LiteralPath $temporaryPackagePath -Force }
    if (Test-Path -LiteralPath $temporaryHashPath) { Remove-Item -LiteralPath $temporaryHashPath -Force }
}
