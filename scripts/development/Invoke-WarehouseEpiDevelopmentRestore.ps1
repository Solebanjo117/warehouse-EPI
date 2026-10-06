#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-WarehouseEpiDevelopmentRestore([object]$Context, [string[]]$MigrationIds,
    [scriptblock]$StopApplication, [scriptblock]$StartApplication) {
    Assert-DevelopmentRestoreCluster $Context
    $root = $Context.Root
    $queue = Resolve-DevelopmentRestorePath (Join-Path $root 'ManualBackups\.restore') $root
    $pending = Join-Path $queue 'pending.json'; $maintenance = Join-Path $queue 'maintenance.json'
    Write-WarehouseEpiRestoreJson (Join-Path $queue 'agent.json') @{ SchemaVersion = 1; CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    if (-not (Test-Path -LiteralPath $pending) -and -not (Test-Path -LiteralPath $maintenance)) { return }
    $job = $null; $journal = $null; $work = $null; $hadJournal = $false; $finished = $false
    $locks = [Collections.Generic.List[IDisposable]]::new()
    function Set-DevelopmentState([int]$State, [string]$Message) {
        $job.State = $State; $job.Error = $Message
        Write-WarehouseEpiRestoreJson $statusPath $job
    }
    function Replace-DevelopmentDirectory([string]$Source, [string]$Target) {
        Remove-DevelopmentRestoreTree $Target $root
        Copy-DevelopmentRestoreDirectory $Source $Target $root
    }
    function Undo-DevelopmentImport {
        & $StopApplication
        if ($journal.Phase -in @('Mutating', 'Completed')) {
            Undo-WarehouseEpiRestoreDatabase $Context
            foreach ($folder in @('Branding', 'WarehouseMapReferences')) {
                Replace-DevelopmentDirectory (Join-Path $work "previous\$folder") (Join-Path $root $folder)
            }
            Write-WarehouseEpiRestoreConfiguration $Context.ConfigurationPath ([IO.File]::ReadAllText((Join-Path $work 'previous\config.json')))
        }
        & $StartApplication
    }
    function Complete-DevelopmentImport {
        foreach ($file in Get-ChildItem -LiteralPath $inputDirectory -File -Force) {
            if ($file.Name -cne 'status.json') { Remove-Item -LiteralPath $file.FullName -Force }
        }
        foreach ($name in @('candidate.pgpass', 'app.pgpass')) {
            $path = Join-Path $work $name
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        if (Test-Path -LiteralPath (Join-Path $work 'incoming')) { Remove-DevelopmentRestoreTree (Join-Path $work 'incoming') $root }
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
        if (Test-Path -LiteralPath $maintenance) { Remove-Item -LiteralPath $maintenance -Force }
        # Nested functions have their own scope; record completion in this function's scope.
        Set-Variable -Name finished -Value $true -Scope 1
    }
    try {
        $locks.Add([WarehouseEpiRestoreProcess]::LockDirectory($queue))
        $interrupted = Test-Path -LiteralPath $maintenance
        if (-not $interrupted) {
            $request = [WarehouseEpiRestoreProcess]::ReadRequest($pending) | ConvertFrom-Json
            Write-WarehouseEpiRestoreJson $maintenance $request
        }
        $request = [WarehouseEpiRestoreProcess]::ReadRequest($maintenance) | ConvertFrom-Json
        $id = [Guid]::Parse($request.Id).ToString('N')
        if ([Guid]::Parse($request.ActorId) -eq [Guid]::Empty -or $request.PackageSha256 -cnotmatch '^[a-f0-9]{64}$' -or
            $request.AdminPinLookup -cnotmatch '^[a-f0-9]{64}$') { throw 'Solicitud de desarrollo inválida.' }
        $inputDirectory = Resolve-DevelopmentRestorePath (Join-Path $queue $id) $root
        $locks.Add([WarehouseEpiRestoreProcess]::LockDirectory($inputDirectory))
        $statusPath = Resolve-DevelopmentRestorePath (Join-Path $inputDirectory 'status.json') $root
        if ((Get-Item -LiteralPath $statusPath).Length -gt 65536) { throw 'Estado demasiado grande.' }
        $job = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        if ([Guid]::Parse($job.Id).ToString('N') -cne $id -or [Guid]::Parse($job.ActorId) -ne [Guid]::Parse($request.ActorId)) { throw 'Estado de desarrollo inválido.' }
        $work = Resolve-DevelopmentRestorePath (Join-Path $root "Recovery\$id") $root
        $null = New-Item -ItemType Directory -Path $work -Force
        foreach ($property in @{ Candidate = "warehouseEPI_restore_$id"; Before = "warehouseEPI_before_$id";
            Failed = "warehouseEPI_failed_$id"; Owner = "epi_restore_$id"; WorkDirectory = $work }.GetEnumerator()) {
            $Context | Add-Member -NotePropertyName $property.Key -NotePropertyValue $property.Value -Force
        }
        $journalPath = Join-Path $work 'journal.json'
        $hadJournal = Test-Path -LiteralPath $journalPath
        if ($hadJournal) {
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
            if ($journal.SchemaVersion -ne 1 -or $journal.Phase -notin @('Preparing', 'Mutating', 'Completed')) {
                $journal = $null
                throw 'El registro de recuperación no es válido.'
            }
        }
        if ($interrupted) {
            if ($job.State -eq 6) { throw 'La recuperación de desarrollo requiere revisión.' }
            if ($journal -and $journal.Phase -eq 'Completed' -and $job.State -eq 4) {
                & $StopApplication; & $StartApplication
            } else {
                if ($journal) { Undo-DevelopmentImport }
                Set-DevelopmentState 5 'La importación no se completó. Se conservó el estado anterior.'
            }
            Complete-DevelopmentImport
            return
        }
        $confirmed = if ($request.ConfirmedAt -is [DateTime]) { [DateTimeOffset]$request.ConfirmedAt }
            else { [DateTimeOffset]::Parse([string]$request.ConfirmedAt, [Globalization.CultureInfo]::InvariantCulture) }
        if ($job.State -ne 2 -or $confirmed -lt [DateTimeOffset]::UtcNow.AddMinutes(-5) -or $confirmed -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) { throw 'La solicitud venció.' }
        if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root)).AvailableFreeSpace -lt 6GB) { throw 'Se requieren al menos 6 GB libres.' }
        Set-DevelopmentState 3 $null
        $incoming = Join-Path $work 'incoming'; $null = New-Item -ItemType Directory -Path $incoming
        foreach ($name in @('migration.zip', 'pinlookupkey.json')) {
            $source = Resolve-DevelopmentRestorePath (Join-Path $inputDirectory $name) $root
            if ((Get-Item -LiteralPath $source).Length -gt $(if ($name -eq 'migration.zip') { 2GB } else { 4096 })) { throw 'Archivo demasiado grande.' }
            Copy-Item -LiteralPath $source -Destination (Join-Path $incoming $name)
        }
        $package = Join-Path $incoming 'migration.zip'
        if ((Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant() -cne $request.PackageSha256) { throw 'El respaldo cambió después de confirmar.' }
        $secrets = Get-Content -LiteralPath (Join-Path $incoming 'pinlookupkey.json') -Raw | ConvertFrom-Json
        if ($secrets.SchemaVersion -ne 1 -or $secrets.MigrationPackageSha256 -cne $request.PackageSha256 -or
            [Convert]::FromBase64String($secrets.PinLookupKey).Length -lt 32) { throw 'La clave no corresponde al respaldo.' }
        $expanded = Join-Path $incoming 'expanded'
        Expand-WarehouseEpiRestoreZip $package $expanded '^(manifest\.json|RESTORE\.txt|database/warehouseEPI-[0-9]{8}-[0-9]{6}\.dump|references/warehouseEPI-[0-9]{8}-[0-9]{6}-references\.zip|branding/[a-f0-9]{32}\.(png|jpg|webp))$'
        $manifest = & (Join-Path $PSScriptRoot '..\security\Test-WarehouseEpiMigrationBackup.ps1') -PackagePath $package
        if ($manifest.DatabaseName -cne 'warehouseEPI') { throw 'El respaldo no corresponde a Warehouse EPI.' }
        $dump = Join-Path $expanded (@($manifest.Files | Where-Object Kind -CEQ 'database')[0].Path)
        $references = Join-Path $expanded (@($manifest.Files | Where-Object Kind -CEQ 'references')[0].Path)
        if ((Get-Item -LiteralPath $references).Length -gt 256MB) { throw 'El croquis excede el límite.' }
        $assets = Join-Path $incoming 'assets'; $null = New-Item -ItemType Directory -Path (Join-Path $assets 'branding') -Force
        Expand-WarehouseEpiRestoreZip $references (Join-Path $assets 'references') '^(manifest\.json|[a-f0-9]{64}\.(png|jpg|webp))$'
        $referenceManifest = Get-Content -LiteralPath (Join-Path $assets 'references\manifest.json') -Raw | ConvertFrom-Json
        if ($referenceManifest.SchemaVersion -ne 1 -or $referenceManifest.DatabaseBackup -cne (Split-Path -Leaf $dump)) { throw 'El croquis no corresponde a la base.' }
        Remove-Item -LiteralPath (Join-Path $assets 'references\manifest.json')
        foreach ($file in @($manifest.Files | Where-Object Kind -CEQ 'branding')) { Copy-Item -LiteralPath (Join-Path $expanded $file.Path) -Destination (Join-Path $assets 'branding') }
        $rolePass = New-WarehouseEpiRestoreCandidate $Context $dump
        Test-WarehouseEpiRestoreCandidate $Context $rolePass $MigrationIds $request.AdminPinLookup $assets
        $journal = [pscustomobject]@{ SchemaVersion = 1; Phase = 'Preparing' }
        Write-WarehouseEpiRestoreJson $journalPath $journal
        & $StopApplication
        $previous = Join-Path $work 'previous'; $null = New-Item -ItemType Directory -Path $previous
        Copy-Item -LiteralPath $Context.ConfigurationPath -Destination (Join-Path $previous 'config.json')
        foreach ($folder in @('Branding', 'WarehouseMapReferences')) { Copy-DevelopmentRestoreDirectory (Join-Path $root $folder) (Join-Path $previous $folder) $root }
        $null = Invoke-WarehouseEpiRestoreTool $Context $Context.PgDump @("--host=$($Context.Host)", "--port=$($Context.Port)",
            "--username=$($Context.AdminUser)", "--dbname=$($Context.Database)", '--format=custom', '--no-owner', '--no-privileges',
            "--file=$(Join-Path $previous 'database.dump')") $Context.AdminPassFile 1800
        foreach ($file in Get-ChildItem -LiteralPath $previous -Recurse -File) { Sync-WarehouseEpiRestoreFile $file.FullName }
        $journal.Phase = 'Mutating'; Write-WarehouseEpiRestoreJson $journalPath $journal
        Switch-WarehouseEpiRestoreDatabase $Context
        foreach ($pair in @(@('branding', 'Branding'), @('references', 'WarehouseMapReferences'))) {
            Replace-DevelopmentDirectory (Join-Path $assets $pair[0]) (Join-Path $root $pair[1])
        }
        $Context.Configuration.Security.PinLookupKey = $secrets.PinLookupKey
        Write-WarehouseEpiRestoreConfiguration $Context.ConfigurationPath ($Context.Configuration | ConvertTo-Json -Depth 20)
        & $StartApplication
        $connection = [Data.Common.DbConnectionStringBuilder]::new(); $connection.set_ConnectionString($Context.Configuration.ConnectionStrings.Warehouse)
        $escaped = ([string]$connection['Password']).Replace('\', '\\').Replace(':', '\:')
        $appPass = Join-Path $work 'app.pgpass'
        [IO.File]::WriteAllText($appPass, "$($Context.Host):$($Context.Port):$($Context.Database):$($Context.AppRole):$escaped`n")
        $null = Invoke-WarehouseEpiRestoreSql $Context $Context.Database 'SELECT count(*) FROM public.users;' $Context.AppRole $appPass
        $journal.Phase = 'Completed'; Write-WarehouseEpiRestoreJson $journalPath $journal
        Set-DevelopmentState 4 $null
        Complete-DevelopmentImport
    }
    catch {
        if (-not $job -or ($hadJournal -and -not $journal) -or $job.State -eq 6) {
            if ($job) { Set-DevelopmentState 6 'IT debe revisar la recuperación.' }
            throw 'La recuperación de desarrollo requiere revisión en artifacts\local-restore.'
        }
        try {
            if ($journal) { Undo-DevelopmentImport }
            Set-DevelopmentState 5 'La importación no se completó. Se conservó el estado anterior.'
            Complete-DevelopmentImport
        } catch {
            Set-DevelopmentState 6 'IT debe revisar la recuperación.'
            throw 'La recuperación de desarrollo requiere revisión en artifacts\local-restore.'
        }
    }
    finally {
        if ($finished) { try { Remove-WarehouseEpiRestoreCandidate $Context } catch { } }
        foreach ($handle in $locks) { $handle.Dispose() }
    }
}
