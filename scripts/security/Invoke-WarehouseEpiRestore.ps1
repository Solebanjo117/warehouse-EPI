#Requires -Version 7.4
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Restore.Common.ps1')
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')

$root = 'C:\ProgramData\WarehouseEPI'
$queueRoot = Join-Path $root 'ManualBackups\.restore'
$maintenance = Join-Path $queueRoot 'maintenance.json'
$pending = Join-Path $queueRoot 'pending.json'
$recoveryRoot = Join-Path $root 'Recovery'
$request = $null; $job = $null; $context = $null; $journal = $null; $stage = 'Prerequisites'
$statusPath = $null; $work = $null; $finished = $false
$hadJournal = $false
$locks = [Collections.Generic.List[IDisposable]]::new()

function Assert-RestorePath([string]$Path) {
    $full = Resolve-WarehouseEpiChildPath $Path $root
    for ($parent = [IO.DirectoryInfo]::new($full); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'No se permiten enlaces.' }
    }
    return $full
}
function Remove-RestoreTree([string]$Path) {
    $full = Assert-RestorePath $Path
    if (Test-Path -LiteralPath $full) {
        Assert-WarehouseEpiTreeHasNoReparsePoints $full
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
function Set-RestoreState([int]$State, [string]$ErrorMessage) {
    $job.State = $State; $job.Error = $ErrorMessage
    Write-WarehouseEpiRestoreJson $statusPath $job
}
function Copy-RestoreDirectory([string]$Source, [string]$Destination) {
    $null = Assert-RestorePath $Source; $null = Assert-RestorePath $Destination
    Assert-WarehouseEpiTreeHasNoReparsePoints $Source
    $null = New-Item -ItemType Directory -Path $Destination -Force
    foreach ($child in Get-ChildItem -LiteralPath $Source -Force) { Copy-Item -LiteralPath $child.FullName -Destination $Destination -Recurse }
}
function Replace-RestoreDirectory([string]$Source, [string]$Destination) {
    Remove-RestoreTree $Destination
    Copy-RestoreDirectory $Source $Destination
    Set-WarehouseEpiRestorePrivateAcl $Destination -ServiceModify
}
function Restore-PreviousInstallation {
    Stop-WarehouseEpiServiceSafely
    if ($journal -and $journal.Phase -in @('Mutating', 'Completed')) {
        Undo-WarehouseEpiRestoreDatabase $context
        foreach ($name in @('Branding', 'WarehouseMapReferences', 'DataProtection-Keys')) {
            Replace-RestoreDirectory (Join-Path (Join-Path $work 'previous') $name) (Join-Path $root $name)
        }
        Write-WarehouseEpiRestoreConfiguration $script:WarehouseEpiConfigPath ([IO.File]::ReadAllText((Join-Path $work 'previous\service-settings.json')))
    }
    Start-WarehouseEpiServiceAndVerify
}
function Complete-RestoreRequest {
    # Discard upload/decrypted staging. Retain status and the protected previous installation.
    foreach ($file in Get-ChildItem -LiteralPath $inputDirectory -File -Force) {
        if ($file.Name -cne 'status.json') { Remove-Item -LiteralPath $file.FullName -Force }
    }
    if (Test-Path -LiteralPath (Join-Path $work 'candidate.pgpass')) { Remove-Item -LiteralPath (Join-Path $work 'candidate.pgpass') -Force }
    if (Test-Path -LiteralPath (Join-Path $work 'app.pgpass')) { Remove-Item -LiteralPath (Join-Path $work 'app.pgpass') -Force }
    if (Test-Path -LiteralPath (Join-Path $work 'incoming')) { Remove-RestoreTree (Join-Path $work 'incoming') }
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    if (Test-Path -LiteralPath $maintenance) { Remove-Item -LiteralPath $maintenance -Force }
    $script:finished = $true
}

try {
    Assert-WarehouseEpiAdministrator
    $null = Assert-RestorePath $queueRoot
    $null = New-Item -ItemType Directory -Path $queueRoot -Force
    $locks.Add([WarehouseEpiRestoreProcess]::LockDirectory((Split-Path -Parent $queueRoot)))
    $locks.Add([WarehouseEpiRestoreProcess]::LockDirectory($queueRoot))
    $service = Get-WarehouseEpiService
    $releaseDirectory = Test-WarehouseEpiReleaseDirectory (Split-Path -Parent (Get-WarehouseEpiExecutableFromService $service))
    $release = Get-Content -LiteralPath (Join-Path $releaseDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
    $configuration = Get-Content -LiteralPath $script:WarehouseEpiConfigPath -Raw | ConvertFrom-Json
    $connection = [Data.Common.DbConnectionStringBuilder]::new(); $connection.set_ConnectionString($configuration.ConnectionStrings.Warehouse)
    if ($connection['Host'] -ine 'localhost' -or $connection['Database'] -cne 'warehouseEPI' -or
        $connection['Username'] -cne 'warehouse_epi_app' -or ($connection.ContainsKey('Port') -and [int]$connection['Port'] -ne 5432)) {
        throw 'La restauración requiere el destino local estándar warehouseEPI y su rol mínimo.'
    }
    if ($configuration.Security.DataProtectionKeysPath -ine (Join-Path $root 'DataProtection-Keys') -or
        ($configuration.PSObject.Properties['Branding'] -and $configuration.Branding.PSObject.Properties['StorageDirectory'] -and $configuration.Branding.StorageDirectory -ine (Join-Path $root 'Branding')) -or
        ($configuration.PSObject.Properties['WarehouseMap'] -and $configuration.WarehouseMap.PSObject.Properties['ReferenceStorageDirectory'] -and $configuration.WarehouseMap.ReferenceStorageDirectory -ine (Join-Path $root 'WarehouseMapReferences')) -or
        ($configuration.PSObject.Properties['Backups'] -and $configuration.Backups.PSObject.Properties['ManualDirectory'] -and $configuration.Backups.ManualDirectory -ine (Join-Path $root 'ManualBackups'))) {
        throw 'La restauración requiere las carpetas estándar de la instalación.'
    }
    $pgBin = 'C:\Program Files\PostgreSQL\18\bin'
    foreach ($tool in @('psql.exe', 'pg_restore.exe', 'pg_dump.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $pgBin $tool) -PathType Leaf)) { throw 'Faltan herramientas PostgreSQL 18.' }
    }
    $passFile = Join-Path $root 'BackupCredentials\postgresql-backup.pgpass'
    if (-not (Test-Path -LiteralPath $passFile -PathType Leaf)) { throw 'Falta el archivo privado de credenciales de mantenimiento.' }
    Write-WarehouseEpiRestoreJson (Join-Path $queueRoot 'agent.json') @{ SchemaVersion = 1; CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $interrupted = Test-Path -LiteralPath $maintenance
    if (-not $interrupted) {
        if (-not (Test-Path -LiteralPath $pending)) { return }
        $requestText = [WarehouseEpiRestoreProcess]::ReadRequest($pending)
        $parsedRequest = $requestText | ConvertFrom-Json
        # Keep pending until completion: the maintenance gate must never disappear during publication.
        Write-WarehouseEpiRestoreJson $maintenance $parsedRequest
    }
    $stage = 'Request'
    $request = [WarehouseEpiRestoreProcess]::ReadRequest($maintenance) | ConvertFrom-Json
    $id = [Guid]::Parse($request.Id).ToString('N')
    if ([Guid]::Parse($request.ActorId) -eq [Guid]::Empty -or $request.PackageSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        $request.AdminPinLookup -cnotmatch '^[a-f0-9]{64}$') { throw 'Solicitud inválida.' }
    $inputDirectory = Assert-RestorePath (Join-Path $queueRoot $id)
    $locks.Add([WarehouseEpiRestoreProcess]::LockDirectory($inputDirectory))
    Assert-WarehouseEpiTreeHasNoReparsePoints $inputDirectory
    $statusPath = Join-Path $inputDirectory 'status.json'
    if ((Get-Item -LiteralPath $statusPath).Length -gt 65536) { throw 'Estado demasiado grande.' }
    $job = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ([Guid]::Parse($job.Id).ToString('N') -cne $id -or $job.ActorId -ne $request.ActorId) { throw 'Estado inválido.' }
    $null = Assert-RestorePath $recoveryRoot
    $null = New-Item -ItemType Directory -Path $recoveryRoot -Force
    Set-WarehouseEpiRestorePrivateAcl $recoveryRoot
    $work = Assert-RestorePath (Join-Path $recoveryRoot $id)
    $null = New-Item -ItemType Directory -Path $work -Force
    $context = [pscustomobject]@{ Host = 'localhost'; Port = 5432; AdminUser = 'postgres'; AdminPassFile = $passFile
        Psql = (Join-Path $pgBin 'psql.exe'); PgRestore = (Join-Path $pgBin 'pg_restore.exe'); PgDump = (Join-Path $pgBin 'pg_dump.exe')
        Database = 'warehouseEPI'; AppRole = 'warehouse_epi_app'; WorkDirectory = $work
        Candidate = "warehouseEPI_restore_$id"; Before = "warehouseEPI_before_$id"; Failed = "warehouseEPI_failed_$id"; Owner = "epi_restore_$id" }
    $journalPath = Join-Path $work 'journal.json'
    $hadJournal = Test-Path -LiteralPath $journalPath
    if ($hadJournal) { $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json }
    if ($interrupted) {
        if ($job.State -eq 6) { return } # IT intervention is required; never replay a destructive request.
        $stage = 'InterruptedRecovery'
        if ($journal -and $journal.Phase -eq 'Completed' -and $job.State -eq 4) {
            Start-WarehouseEpiServiceAndVerify
            Complete-RestoreRequest
            return
        }
        Restore-PreviousInstallation
        Set-RestoreState 5 'La restauración se interrumpió. Se recuperó el estado anterior.'
        Complete-RestoreRequest
        return
    }
    # Newer PowerShell converts ISO JSON dates to DateTime. Avoid a culture-dependent string roundtrip.
    $confirmedAt = if ($request.ConfirmedAt -is [DateTime]) { [DateTimeOffset]$request.ConfirmedAt }
        else { [DateTimeOffset]::Parse([string]$request.ConfirmedAt, [Globalization.CultureInfo]::InvariantCulture) }
    if ($job.State -ne 2 -or $confirmedAt -lt [DateTimeOffset]::UtcNow.AddMinutes(-5) -or $confirmedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) { throw 'La solicitud venció.' }
    if ([IO.DriveInfo]::new('C:\').AvailableFreeSpace -lt 6GB) { throw 'Se requieren al menos 6 GB libres para iniciar.' }
    Set-RestoreState 3 $null
    $stage = 'CopyAndValidate'
    $incoming = Join-Path $work 'incoming'; $null = New-Item -ItemType Directory -Path $incoming
    # Copy to SYSTEM-only storage before interpreting any uploaded content.
    foreach ($name in @('migration.zip', 'pinlookupkey.json')) {
        $source = Join-Path $inputDirectory $name
        if ((Get-Item -LiteralPath $source).Length -gt $(if ($name -eq 'migration.zip') { 2GB } else { 4096 })) { throw 'Archivo demasiado grande.' }
        Copy-Item -LiteralPath $source -Destination (Join-Path $incoming $name)
    }
    $package = Join-Path $incoming 'migration.zip'
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -cne $request.PackageSha256) { throw 'El respaldo cambió después de confirmarlo.' }
    $secrets = Get-Content -LiteralPath (Join-Path $incoming 'pinlookupkey.json') -Raw | ConvertFrom-Json
    if ($secrets.SchemaVersion -ne 1 -or $secrets.MigrationPackageSha256 -cne $request.PackageSha256 -or
        [Convert]::FromBase64String($secrets.PinLookupKey).Length -lt 32) { throw 'La clave no corresponde al respaldo.' }
    $expanded = Join-Path $incoming 'expanded'
    Expand-WarehouseEpiRestoreZip $package $expanded '^(manifest\.json|RESTORE\.txt|database/warehouseEPI-[0-9]{8}-[0-9]{6}\.dump|references/warehouseEPI-[0-9]{8}-[0-9]{6}-references\.zip|branding/[a-f0-9]{32}\.(png|jpg|webp))$'
    $manifest = & (Join-Path $PSScriptRoot 'Test-WarehouseEpiMigrationBackup.ps1') -PackagePath $package
    if ($manifest.DatabaseName -cne 'warehouseEPI') { throw 'El respaldo no corresponde a warehouseEPI.' }
    $dump = Join-Path $expanded (@($manifest.Files | Where-Object Kind -CEQ 'database')[0].Path)
    $referenceZip = Join-Path $expanded (@($manifest.Files | Where-Object Kind -CEQ 'references')[0].Path)
    if ((Get-Item -LiteralPath $referenceZip).Length -gt 256MB) { throw 'El archivo del croquis excede el límite.' }
    $assets = Join-Path $incoming 'assets'
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $assets 'branding')
    Expand-WarehouseEpiRestoreZip $referenceZip (Join-Path $assets 'references') '^(manifest\.json|[a-f0-9]{64}\.(png|jpg|webp))$'
    $referenceManifest = Get-Content -LiteralPath (Join-Path $assets 'references\manifest.json') -Raw | ConvertFrom-Json
    if ($referenceManifest.SchemaVersion -ne 1 -or $referenceManifest.DatabaseBackup -cne (Split-Path -Leaf $dump)) { throw 'El croquis no corresponde a la base.' }
    Remove-Item -LiteralPath (Join-Path $assets 'references\manifest.json')
    foreach ($file in @($manifest.Files | Where-Object Kind -CEQ 'branding')) { Copy-Item -LiteralPath (Join-Path $expanded $file.Path) -Destination (Join-Path $assets 'branding') }
    $stage = 'Candidate'
    $rolePass = New-WarehouseEpiRestoreCandidate $context $dump
    Test-WarehouseEpiRestoreCandidate $context $rolePass @($release.migrationIds) $request.AdminPinLookup $assets
    $stage = 'Snapshot'
    $journal = [pscustomobject]@{ SchemaVersion = 1; Id = $id; Phase = 'Preparing'; Stage = $stage; StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    Write-WarehouseEpiRestoreJson $journalPath $journal
    Stop-WarehouseEpiServiceSafely
    $previous = Join-Path $work 'previous'; $null = New-Item -ItemType Directory -Path $previous
    Copy-Item -LiteralPath $script:WarehouseEpiConfigPath -Destination (Join-Path $previous 'service-settings.json')
    foreach ($name in @('Branding', 'WarehouseMapReferences', 'DataProtection-Keys')) { Copy-RestoreDirectory (Join-Path $root $name) (Join-Path $previous $name) }
    $null = Invoke-WarehouseEpiRestoreTool $context $context.PgDump @("--host=$($context.Host)", "--port=$($context.Port)", '--username=postgres',
        '--dbname=warehouseEPI', '--format=custom', '--no-owner', '--no-privileges', "--file=$(Join-Path $previous 'database.dump')") $passFile 1800
    foreach ($file in Get-ChildItem -LiteralPath $previous -File -Recurse) { Sync-WarehouseEpiRestoreFile $file.FullName }
    $journal.Phase = 'Mutating'; Write-WarehouseEpiRestoreJson $journalPath $journal
    $stage = 'Replace'
    Switch-WarehouseEpiRestoreDatabase $context
    foreach ($pair in @(@('branding', 'Branding'), @('references', 'WarehouseMapReferences'))) { Replace-RestoreDirectory (Join-Path $assets $pair[0]) (Join-Path $root $pair[1]) }
    $configuration.Security.PinLookupKey = $secrets.PinLookupKey
    Write-WarehouseEpiRestoreConfiguration $script:WarehouseEpiConfigPath ($configuration | ConvertTo-Json -Depth 20)
    Remove-RestoreTree (Join-Path $root 'DataProtection-Keys')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'DataProtection-Keys')
    Set-WarehouseEpiRestorePrivateAcl (Join-Path $root 'DataProtection-Keys') -ServiceModify
    $stage = 'Start'
    Start-WarehouseEpiServiceAndVerify
    $appPass = Join-Path $work 'app.pgpass'
    $escaped = ([string]$connection['Password']).Replace('\', '\\').Replace(':', '\:')
    [IO.File]::WriteAllText($appPass, "localhost:5432:warehouseEPI:warehouse_epi_app:$escaped`n")
    $null = Invoke-WarehouseEpiRestoreSql $context warehouseEPI 'SELECT count(*) FROM public.users;' warehouse_epi_app $appPass
    $journal.Phase = 'Completed'; Write-WarehouseEpiRestoreJson $journalPath $journal
    Set-RestoreState 4 $null
    Complete-RestoreRequest
}
catch {
    # Logs contain only a fixed stage/type, never SQL, credentials, NIP, or uploaded values.
    try { Write-WarehouseEpiRestoreJson (Join-Path $queueRoot 'agent-error.json') @{ Stage = $stage; Type = $_.Exception.GetType().Name; AtUtc = [DateTimeOffset]::UtcNow.ToString('o') } } catch { }
    if ($work) { Write-WarehouseEpiRestoreJson (Join-Path $work 'failure.json') @{ Stage = $stage; Type = $_.Exception.GetType().Name; AtUtc = [DateTimeOffset]::UtcNow.ToString('o') } }
    if ($job -and $context) {
        try {
            if ($hadJournal -and -not $journal) { throw 'El registro de recuperación requiere revisión.' }
            if ($journal) { Restore-PreviousInstallation }
            Set-RestoreState 5 'La importación no se completó. Se conservó el estado anterior.'
            Complete-RestoreRequest
        }
        catch {
            Set-RestoreState 6 'IT debe revisar la recuperación.'
            # Leave the maintenance gate and all protected copies for IT. Never discard the original DB.
        }
    }
    # Invalid/corrupt requests fail closed and require IT to remove the gate after investigation.
}
finally {
    if ($context -and $finished) { try { Remove-WarehouseEpiRestoreCandidate $context } catch { } }
    foreach ($handle in $locks) { $handle.Dispose() }
}
