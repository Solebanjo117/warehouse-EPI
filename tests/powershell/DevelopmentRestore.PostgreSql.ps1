#Requires -Version 7.4
param([string]$RepositoryRoot, [string]$FixtureRoot, [string]$PackagePath, [string]$KeyPath, [string]$MigrationIdsPath, [string]$WebDllPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ((Split-Path -Leaf $FixtureRoot) -cne 'development-fixture' -or
    (Split-Path -Leaf (Split-Path -Parent $FixtureRoot)) -notlike 'warehouse-epi-backup-test-*') { throw 'Requires a private backup test fixture.' }
. (Join-Path $RepositoryRoot 'scripts\development\WarehouseEpi.DevelopmentRestore.Common.ps1')
. (Join-Path $RepositoryRoot 'scripts\development\Invoke-WarehouseEpiDevelopmentRestore.ps1')
function Assert-DevelopmentFixture([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$root = Join-Path $FixtureRoot 'artifacts\local-restore'
$null = New-Item -ItemType Directory -Path $root -Force
Set-DevelopmentRestorePrivateAcl $root
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
if ($port -eq 5432) { throw 'Production port is prohibited.' }
$configPath = Join-Path $root 'config.json'
$originalKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$backupKey = (Get-Content -LiteralPath $KeyPath -Raw | ConvertFrom-Json).PinLookupKey
$settings = @{
    ServiceConfigPath = ''
    AllowedHosts = 'localhost;127.0.0.1'
    ConnectionStrings = @{ Warehouse = "Host=127.0.0.1;Port=$port;Database=warehouse_epi_restore_dev;Username=warehouse_epi_dev_app;Password=fixture" }
    Security = @{ PinLookupKey = $originalKey }
    Development = @{ PostgreSqlPort = $port; UseEphemeralDataProtection = $true }
    Backups = @{ ManualDirectory = (Join-Path $root 'ManualBackups') }
    Branding = @{ StorageDirectory = (Join-Path $root 'Branding') }
    WarehouseMap = @{ ReferenceStorageDirectory = (Join-Path $root 'WarehouseMapReferences') }
    Observability = @{ LogDirectory = (Join-Path $root 'Logs') }
}
Write-WarehouseEpiRestoreJson $configPath $settings
[IO.File]::WriteAllText((Join-Path $root 'owner.pgpass'), "127.0.0.1:${port}:*:warehouse_epi_dev_owner:`n")
$context = New-DevelopmentRestoreContext $root
$bin = 'C:\Program Files\PostgreSQL\18\bin'
$cluster = Join-Path $root 'postgres'
$global:DevelopmentFixtureApplication = $null
$webListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$webListener.Start(); $webPort = $webListener.LocalEndpoint.Port; $webListener.Stop()
$contentRoot = Join-Path $FixtureRoot 'src\WarehouseEPI.Web'; $null = New-Item -ItemType Directory -Path $contentRoot -Force
$dotnetPath = (Get-Command dotnet).Source
function Stop-FixtureDevelopmentApplication {
    if ($global:DevelopmentFixtureApplication -and -not $global:DevelopmentFixtureApplication.HasExited) {
        $global:DevelopmentFixtureApplication.Kill($true); $global:DevelopmentFixtureApplication.WaitForExit()
    }
    if ($global:DevelopmentFixtureApplication) { $global:DevelopmentFixtureApplication.Dispose(); $global:DevelopmentFixtureApplication = $null }
}
function Start-FixtureDevelopmentApplication {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    foreach ($argument in @($WebDllPath, '--environment=Development', "--contentRoot=$contentRoot", "--urls=http://127.0.0.1:$webPort",
        "--Development:RestoreConfigPath=$configPath", '--Logging:LogLevel:Default=Warning')) { $startInfo.ArgumentList.Add($argument) }
    $global:DevelopmentFixtureApplication = [Diagnostics.Process]::Start($startInfo)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $lastStatus = 'no response'
    do {
        if ($global:DevelopmentFixtureApplication.HasExited) { throw 'Fixture development web app failed.' }
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$webPort/health/live" -TimeoutSec 2 -SkipHttpErrorCheck -NoProxy
            $lastStatus = [string]$response.StatusCode
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Fixture development web app startup timed out: $lastStatus."
}
& (Join-Path $bin 'initdb.exe') -D $cluster -U warehouse_epi_dev_owner --auth=trust --encoding=UTF8 --locale=C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private initdb failed.' }
$started = Invoke-DevelopmentClusterControl (Join-Path $bin 'pg_ctl.exe') @('-D', $cluster, '-l', (Join-Path $root 'postgres.log'), '-o', "-h 127.0.0.1 -p $port", '-w', 'start')
if ($started -ne 0) { throw 'Private PostgreSQL failed to start.' }
try {
    Assert-DevelopmentRestoreCluster $context
    foreach ($folder in @('ManualBackups\.restore', 'Branding', 'WarehouseMapReferences', 'Recovery')) { $null = New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force }
    $null = Invoke-WarehouseEpiRestoreSql $context postgres 'CREATE ROLE warehouse_epi_dev_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;'
    $null = Invoke-WarehouseEpiRestoreSql $context postgres 'CREATE DATABASE warehouse_epi_restore_dev OWNER warehouse_epi_dev_app;'
    $null = Invoke-WarehouseEpiRestoreSql $context postgres 'CREATE DATABASE "warehouseEPI";'
    $null = Invoke-WarehouseEpiRestoreSql $context warehouseEPI "CREATE TABLE production_sentinel (value text); INSERT INTO production_sentinel VALUES ('untouched');"
    $expanded = Join-Path $root 'fixture-package'
    Expand-WarehouseEpiRestoreZip $PackagePath $expanded '^(manifest\.json|RESTORE\.txt|database/warehouseEPI-[0-9]{8}-[0-9]{6}\.dump|references/warehouseEPI-[0-9]{8}-[0-9]{6}-references\.zip|branding/[a-f0-9]{32}\.(png|jpg|webp))$'
    $manifest = Get-Content -LiteralPath (Join-Path $expanded 'manifest.json') -Raw | ConvertFrom-Json
    $dump = Join-Path $expanded (@($manifest.Files | Where-Object Kind -CEQ 'database')[0].Path)
    $null = Invoke-WarehouseEpiRestoreTool $context $context.PgRestore @('--no-owner', '--no-privileges', '--exit-on-error',
        '--host=127.0.0.1', "--port=$port", '--username=warehouse_epi_dev_owner', '--dbname=warehouse_epi_restore_dev', $dump) $context.AdminPassFile
    $null = Invoke-WarehouseEpiRestoreSql $context $context.Database 'GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO warehouse_epi_dev_app; GRANT USAGE,SELECT,UPDATE ON ALL SEQUENCES IN SCHEMA public TO warehouse_epi_dev_app;'
    $null = Invoke-WarehouseEpiRestoreTool $context $dotnetPath @($WebDllPath, '--environment=Development', "--contentRoot=$contentRoot",
        "--Development:RestoreConfigPath=$configPath", '--prepare-development-restore', '--Logging:LogLevel:Default=Warning') $context.AdminPassFile
    $migrationIds = @(Get-Content -LiteralPath $MigrationIdsPath -Raw | ConvertFrom-Json)
    $global:DevelopmentRestoreFixture = @{ Stops = 0; Starts = 0; FailStart = $false }
    $stop = { $global:DevelopmentRestoreFixture.Stops++; Stop-FixtureDevelopmentApplication }
    $start = { $global:DevelopmentRestoreFixture.Starts++
        if ($global:DevelopmentRestoreFixture.FailStart) { $global:DevelopmentRestoreFixture.FailStart = $false; throw 'Fixture startup failure.' }
        Start-FixtureDevelopmentApplication
    }
    function Prepare-DevelopmentFixture {
        $settings.Security.PinLookupKey = $originalKey
        Write-WarehouseEpiRestoreJson $configPath $settings
        $lookup = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Convert]::FromBase64String($originalKey), [Text.Encoding]::UTF8.GetBytes('1470'))).ToLowerInvariant()
        $null = Invoke-WarehouseEpiRestoreSql $context $context.Database "UPDATE public.users SET full_name='Local current data', pin_lookup='$lookup';"
        [IO.File]::WriteAllText((Join-Path $root 'Branding\previous.txt'), 'previous development branding')
        $global:DevelopmentRestoreFixture.Stops = 0; $global:DevelopmentRestoreFixture.Starts = 0
    }
    function Prepare-DevelopmentRequest([string]$Pin = '1470') {
        $id = [Guid]::NewGuid(); $actor = [Guid]::NewGuid()
        $directory = Join-Path $root "ManualBackups\.restore\$($id.ToString('N'))"
        $null = New-Item -ItemType Directory -Path $directory
        Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $directory 'migration.zip')
        Copy-Item -LiteralPath $KeyPath -Destination (Join-Path $directory 'pinlookupkey.json')
        Write-WarehouseEpiRestoreJson (Join-Path $directory 'status.json') @{ Id = $id; ActorId = $actor; State = 2; Error = $null }
        Write-WarehouseEpiRestoreJson (Join-Path $root 'ManualBackups\.restore\pending.json') @{ Id = $id; ActorId = $actor
            ConfirmedAt = [DateTimeOffset]::UtcNow.ToString('o'); PackageSha256 = (Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant()
            AdminPinLookup = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Convert]::FromBase64String($backupKey), [Text.Encoding]::UTF8.GetBytes($Pin))).ToLowerInvariant() }
        return $directory
    }
    function Assert-ProductionSentinel {
        Assert-DevelopmentFixture ((Invoke-WarehouseEpiRestoreSql $context warehouseEPI 'SELECT value FROM production_sentinel;') -ceq 'untouched') 'Production sentinel changed.'
    }
    Prepare-DevelopmentFixture
    Start-FixtureDevelopmentApplication
    $directory = Prepare-DevelopmentRequest
    Invoke-WarehouseEpiDevelopmentRestore (New-DevelopmentRestoreContext $root) $migrationIds $stop $start
    $status = Get-Content -LiteralPath (Join-Path $directory 'status.json') -Raw | ConvertFrom-Json
    Assert-DevelopmentFixture ($status.State -eq 4) 'Development import did not succeed.'
    Assert-DevelopmentFixture ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Security.PinLookupKey -ceq $backupKey) 'Paired PIN key was not restored.'
    Assert-DevelopmentFixture ($global:DevelopmentRestoreFixture.Stops -eq 1 -and $global:DevelopmentRestoreFixture.Starts -eq 1) 'Only development lifecycle should run.'
    Assert-DevelopmentFixture (-not (Test-Path -LiteralPath (Join-Path $root 'ManualBackups\.restore\pending.json'))) 'Successful gate was not cleared.'
    Assert-DevelopmentFixture (@(Get-ChildItem -LiteralPath $directory -File).Count -eq 1) 'Decrypted upload was not cleaned.'
    Assert-ProductionSentinel
    Prepare-DevelopmentFixture
    $directory = Prepare-DevelopmentRequest
    $global:DevelopmentRestoreFixture.FailStart = $true
    Invoke-WarehouseEpiDevelopmentRestore (New-DevelopmentRestoreContext $root) $migrationIds $stop $start
    $status = Get-Content -LiteralPath (Join-Path $directory 'status.json') -Raw | ConvertFrom-Json
    Assert-DevelopmentFixture ($status.State -eq 5) 'Failed startup did not roll back.'
    Assert-DevelopmentFixture ((Invoke-WarehouseEpiRestoreSql $context $context.Database 'SELECT full_name FROM public.users LIMIT 1;') -ceq 'Local current data') 'Previous development database was not recovered.'
    Assert-DevelopmentFixture ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Security.PinLookupKey -ceq $originalKey) 'Previous development PIN key was not recovered.'
    Assert-DevelopmentFixture (Test-Path -LiteralPath (Join-Path $root 'Branding\previous.txt')) 'Previous assets were not recovered.'
    Assert-ProductionSentinel
    Prepare-DevelopmentFixture
    $directory = Prepare-DevelopmentRequest '0000'
    Invoke-WarehouseEpiDevelopmentRestore (New-DevelopmentRestoreContext $root) $migrationIds $stop $start
    Assert-DevelopmentFixture ((Get-Content -LiteralPath (Join-Path $directory 'status.json') -Raw | ConvertFrom-Json).State -eq 5) 'Wrong PIN should fail.'
    Assert-DevelopmentFixture ($global:DevelopmentRestoreFixture.Stops -eq 0) 'Wrong PIN stopped the application.'
    Assert-ProductionSentinel
    # Simulate process loss after renaming the original database and before activating the candidate.
    Prepare-DevelopmentFixture
    $directory = Prepare-DevelopmentRequest
    $interruptedId = Split-Path -Leaf $directory
    $previous = Join-Path $root "Recovery\$interruptedId\previous"
    $null = New-Item -ItemType Directory -Path $previous -Force
    Copy-Item -LiteralPath $configPath -Destination (Join-Path $previous 'config.json')
    foreach ($folder in @('Branding', 'WarehouseMapReferences')) {
        Copy-DevelopmentRestoreDirectory (Join-Path $root $folder) (Join-Path $previous $folder) $root
    }
    Write-WarehouseEpiRestoreJson (Join-Path $root "Recovery\$interruptedId\journal.json") @{ SchemaVersion = 1; Phase = 'Mutating' }
    $interruptedStatusPath = Join-Path $directory 'status.json'
    $interruptedStatus = Get-Content -LiteralPath $interruptedStatusPath -Raw | ConvertFrom-Json
    $interruptedStatus.State = 3
    Write-WarehouseEpiRestoreJson $interruptedStatusPath $interruptedStatus
    Copy-Item -LiteralPath (Join-Path $root 'ManualBackups\.restore\pending.json') -Destination (Join-Path $root 'ManualBackups\.restore\maintenance.json')
    Stop-FixtureDevelopmentApplication
    $null = Invoke-WarehouseEpiRestoreSql $context postgres "ALTER DATABASE warehouse_epi_restore_dev RENAME TO `"warehouseEPI_before_$interruptedId`";"
    Invoke-WarehouseEpiDevelopmentRestore (New-DevelopmentRestoreContext $root) $migrationIds $stop $start
    Assert-DevelopmentFixture ((Get-Content -LiteralPath $interruptedStatusPath -Raw | ConvertFrom-Json).State -eq 5) 'Interrupted rename should recover the original, never replay the import.'
    Assert-DevelopmentFixture ((Invoke-WarehouseEpiRestoreSql $context $context.Database 'SELECT full_name FROM public.users LIMIT 1;') -ceq 'Local current data') 'Interrupted database was not recovered.'
    Assert-DevelopmentFixture ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Security.PinLookupKey -ceq $originalKey) 'Interrupted PIN key was not recovered.'
    Assert-ProductionSentinel
    # Guard before connecting: production's port must be rejected.
    $settings.Development.PostgreSqlPort = 5432
    Write-WarehouseEpiRestoreJson $configPath $settings
    $rejected = $false
    try { $null = New-DevelopmentRestoreContext $root } catch { $rejected = $true }
    Assert-DevelopmentFixture $rejected 'Production port was accepted.'
    $settings.Development.PostgreSqlPort = $port
    Write-WarehouseEpiRestoreJson $configPath $settings
    # A correctly named database is insufficient: the PostgreSQL data directory must also belong to this root.
    $wrongContext = New-DevelopmentRestoreContext $root
    $wrongContext.Root = Join-Path $FixtureRoot 'other\artifacts\local-restore'
    $rejected = $false
    try { Assert-DevelopmentRestoreCluster $wrongContext } catch { $rejected = $true }
    Assert-DevelopmentFixture $rejected 'Foreign cluster was accepted.'
    Assert-ProductionSentinel
    Write-Output 'development isolation, import and rollback: passed'
}
finally {
    Stop-FixtureDevelopmentApplication
    $null = Invoke-DevelopmentClusterControl (Join-Path $bin 'pg_ctl.exe') @('-D', $cluster, '-w', '-m', 'immediate', 'stop')
}
