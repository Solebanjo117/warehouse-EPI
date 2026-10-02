#Requires -Version 7.4
# This harness is called only with a freshly initialized, private PostgreSQL cluster.
param([string]$RepositoryRoot, [string]$FixtureRoot, [int]$Port, [string]$SourceDatabase,
    [string]$PackagePath, [string]$KeyPath, [string]$LogoPath, [string]$ReferencePath, [string]$MigrationIdsPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$FixtureRoot = [IO.Path]::GetFullPath($FixtureRoot)
if ((Split-Path -Leaf (Split-Path -Parent $FixtureRoot)) -notlike 'warehouse-epi-backup-test-*' -or
    (Split-Path -Leaf $FixtureRoot) -cne 'restore-server-fixture' -or $Port -eq 5432 -or
    $SourceDatabase -cnotmatch '^warehouse_epi_manual_backup_test_[a-f0-9]{32}$') { throw 'The harness requires a private test cluster and fixture directory.' }
$null = New-Item -ItemType Directory -Path $FixtureRoot -Force
$scripts = Join-Path $FixtureRoot 'test-scripts'; $null = New-Item -ItemType Directory -Path $scripts
$safeLiteral = $FixtureRoot.Replace("'", "''")
foreach ($relative in @('scripts/security/WarehouseEpi.Restore.Common.ps1', 'scripts/security/Invoke-WarehouseEpiRestore.ps1',
    'scripts/security/Test-WarehouseEpiMigrationBackup.ps1', 'scripts/release/WarehouseEpi.Release.Common.ps1')) {
    $text = [IO.File]::ReadAllText((Join-Path $RepositoryRoot $relative))
    $text = $text.Replace('C:\ProgramData\WarehouseEPI', $safeLiteral)
    if ($relative.EndsWith('Invoke-WarehouseEpiRestore.ps1')) {
        $text = $text.Replace('#Requires -RunAsAdministrator', '# Private fixture: administrator/service operations are intercepted.')
        $text = $text.Replace('5432', [string]$Port).Replace("AdminUser = 'postgres'", "AdminUser = 'backup_test_admin'")
        $text = $text.Replace('--username=postgres', '--username=backup_test_admin')
        $text = $text.Replace('# Logs contain only a fixed stage/type, never SQL, credentials, NIP, or uploaded values.',
            'Write-Output ("Fixture failure at " + $stage + ": " + $_.Exception.Message + " " + $_.ScriptStackTrace)')
    }
    [IO.File]::WriteAllText((Join-Path $scripts (Split-Path -Leaf $relative)), $text)
}
# Simulate service actions only; the database, snapshot, key/config replacement and rollback remain real.
@'
function Assert-WarehouseEpiAdministrator { }
function Get-WarehouseEpiService { [pscustomobject]@{ PathName = '"' + (Join-Path $script:WarehouseEpiReleasesRoot '0.0.0-fixture\WarehouseEPI.Web.exe') + '"' } }
function Stop-WarehouseEpiServiceSafely { $global:RestoreFixture.Events.Add('stop') }
function Start-WarehouseEpiServiceAndVerify {
    $global:RestoreFixture.Events.Add('start')
    if ($global:RestoreFixture.FailStart) { $global:RestoreFixture.FailStart = $false; throw 'Fixture startup failure.' }
}
'@ | Add-Content -LiteralPath (Join-Path $scripts 'WarehouseEpi.Release.Common.ps1')
# Test processes may not run as SYSTEM/admin. Keep the isolated test tree accessible to its creator.
@'
function Set-WarehouseEpiRestorePrivateAcl([string]$Path, [switch]$ServiceModify, [switch]$ServiceRead) {
    $acl = [Security.AccessControl.DirectorySecurity]::new(); $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
}
'@ | Add-Content -LiteralPath (Join-Path $scripts 'WarehouseEpi.Restore.Common.ps1')
. (Join-Path $scripts 'WarehouseEpi.Restore.Common.ps1')
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
$passFile = Join-Path $FixtureRoot 'BackupCredentials\postgresql-backup.pgpass'
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $passFile)
[IO.File]::WriteAllText($passFile, "localhost:${Port}:*:backup_test_admin:`n")
$context = [pscustomobject]@{ Host = 'localhost'; Port = $Port; AdminUser = 'backup_test_admin'; AdminPassFile = $passFile
    Psql = (Join-Path $pgBin 'psql.exe'); PgRestore = (Join-Path $pgBin 'pg_restore.exe'); WorkDirectory = $FixtureRoot
    Database = 'warehouseEPI'; AppRole = 'warehouse_epi_app' }
$null = Invoke-WarehouseEpiRestoreSql $context postgres 'SHOW server_version_num;'
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $FixtureRoot) 'fixture-tool'))) { throw 'The fixture guard file is missing.' }
$null = Invoke-WarehouseEpiRestoreSql $context postgres "CREATE DATABASE `"warehouseEPI`" TEMPLATE `"$SourceDatabase`";"
$null = Invoke-WarehouseEpiRestoreSql $context postgres 'CREATE ROLE warehouse_epi_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;'
$null = Invoke-WarehouseEpiRestoreSql $context warehouseEPI 'GRANT USAGE ON SCHEMA public TO warehouse_epi_app; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO warehouse_epi_app; GRANT USAGE,SELECT,UPDATE ON ALL SEQUENCES IN SCHEMA public TO warehouse_epi_app;'
$release = Join-Path $FixtureRoot 'Releases\0.0.0-fixture'
$null = New-Item -ItemType Directory -Path $release
$MigrationIds = @(Get-Content -LiteralPath $MigrationIdsPath -Raw | ConvertFrom-Json)
[IO.File]::WriteAllText((Join-Path $release 'WarehouseEPI.Web.exe'), 'fixture')
Write-WarehouseEpiRestoreJson (Join-Path $release 'release-manifest.json') @{ migrationIds = $MigrationIds }
$null = New-Item -ItemType Directory -Path (Join-Path $FixtureRoot 'Config')
$originalKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$backupKey = (Get-Content -LiteralPath $KeyPath -Raw | ConvertFrom-Json).PinLookupKey
$global:RestoreFixture = @{ Events = [Collections.Generic.List[string]]::new(); FailStart = $false }
function Assert-Fixture([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Prepare-LiveState {
    foreach ($name in @('Branding', 'WarehouseMapReferences', 'DataProtection-Keys')) {
        $directory = Join-Path $FixtureRoot $name
        $null = New-Item -ItemType Directory -Path $directory -Force
        [IO.File]::WriteAllText((Join-Path $directory 'previous.txt'), "previous $name")
    }
    Copy-Item -LiteralPath $LogoPath -Destination (Join-Path $FixtureRoot 'Branding') -Force
    Copy-Item -LiteralPath $ReferencePath -Destination (Join-Path $FixtureRoot 'WarehouseMapReferences') -Force
    Write-WarehouseEpiRestoreJson (Join-Path $FixtureRoot 'Config\service-settings.json') @{
        ConnectionStrings = @{ Warehouse = "Host=localhost;Port=$Port;Database=warehouseEPI;Username=warehouse_epi_app;Password=fixture" }
        Security = @{ PinLookupKey = $originalKey; DataProtectionKeysPath = (Join-Path $FixtureRoot 'DataProtection-Keys'); ServerCertificateThumbprint = 'fixture' }
        AllowedHosts = 'fixture.example.com'
    }
    $lookup = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Convert]::FromBase64String($originalKey), [Text.Encoding]::UTF8.GetBytes('1470'))).ToLowerInvariant()
    $null = Invoke-WarehouseEpiRestoreSql $context warehouseEPI "UPDATE public.users SET full_name='Current capture after backup', pin_lookup='$lookup';"
    $global:RestoreFixture.Events.Clear()
}
function Prepare-Request([string]$Pin = '1470') {
    $id = [Guid]::NewGuid(); $actor = [Guid]::NewGuid()
    $directory = Join-Path $FixtureRoot "ManualBackups\.restore\$($id.ToString('N'))"
    $null = New-Item -ItemType Directory -Path $directory -Force
    Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $directory 'migration.zip')
    Copy-Item -LiteralPath $KeyPath -Destination (Join-Path $directory 'pinlookupkey.json')
    $hash = (Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant()
    Write-WarehouseEpiRestoreJson (Join-Path $directory 'status.json') @{ Id = $id; ActorId = $actor; CreatedAt = [DateTimeOffset]::UtcNow.ToString('o'); State = 2; Summary = $null; Error = $null }
    $lookup = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Convert]::FromBase64String($backupKey), [Text.Encoding]::UTF8.GetBytes($Pin))).ToLowerInvariant()
    Write-WarehouseEpiRestoreJson (Join-Path (Split-Path -Parent $directory) 'pending.json') @{
        Id = $id; ActorId = $actor; ConfirmedAt = [DateTimeOffset]::UtcNow.ToString('o'); PackageSha256 = $hash; AdminPinLookup = $lookup }
    return @{ Id = $id.ToString('N'); Directory = $directory }
}
function Invoke-FixtureDriver { & (Join-Path $scripts 'Invoke-WarehouseEpiRestore.ps1') }
function Assert-LiveState {
    Assert-Fixture ((Invoke-WarehouseEpiRestoreSql $context warehouseEPI 'SELECT full_name FROM public.users LIMIT 1;') -ceq 'Current capture after backup') 'Rollback must preserve captures after the backup.'
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $FixtureRoot 'Config\service-settings.json') -Raw | ConvertFrom-Json).Security.PinLookupKey -ceq $originalKey) 'Rollback must restore the previous PIN key.'
    foreach ($name in @('Branding', 'WarehouseMapReferences', 'DataProtection-Keys')) {
        Assert-Fixture (Test-Path -LiteralPath (Join-Path $FixtureRoot "$name\previous.txt")) 'Rollback must restore files and login keys.'
    }
}
try {
    # A dump can execute SQL during refresh. Verify that the candidate login cannot create a privileged role.
    $null = Invoke-WarehouseEpiRestoreSql $context postgres "CREATE DATABASE epi_restore_attack_fixture TEMPLATE `"$SourceDatabase`";"
    $attackSql = @'
CREATE FUNCTION public.fixture_escape() RETURNS integer LANGUAGE plpgsql AS $$
BEGIN EXECUTE 'CREATE ROLE epi_restore_escape_probe SUPERUSER'; RETURN 1; END; $$;
CREATE MATERIALIZED VIEW public.fixture_escape_view AS SELECT public.fixture_escape();
'@
    $null = Invoke-WarehouseEpiRestoreSql $context epi_restore_attack_fixture $attackSql
    $null = Invoke-WarehouseEpiRestoreSql $context postgres 'DROP ROLE epi_restore_escape_probe;'
    $attackDump = Join-Path $FixtureRoot 'attack.dump'
    $null = Invoke-WarehouseEpiRestoreTool $context (Join-Path $pgBin 'pg_dump.exe') @("--host=localhost", "--port=$Port",
        '--username=backup_test_admin', '--dbname=epi_restore_attack_fixture', '--format=custom', '--no-owner', '--no-privileges', "--file=$attackDump") $passFile
    $attackId = [Guid]::NewGuid().ToString('N')
    $attackContext = [pscustomobject]@{ Host = $context.Host; Port = $Port; AdminUser = $context.AdminUser; AdminPassFile = $passFile
        Psql = $context.Psql; PgRestore = $context.PgRestore; WorkDirectory = $FixtureRoot
        Candidate = "warehouseEPI_restore_$attackId"; Owner = "epi_restore_$attackId" }
    $rejected = $false
    try { $null = New-WarehouseEpiRestoreCandidate $attackContext $attackDump } catch { $rejected = $true }
    Assert-Fixture $rejected 'Uploaded SQL must not run with PostgreSQL administrator privileges.'
    Assert-Fixture ((Invoke-WarehouseEpiRestoreSql $context postgres "SELECT count(*) FROM pg_roles WHERE rolname='epi_restore_escape_probe';") -ceq '0') 'Uploaded SQL must not create a privileged role.'
    $null = Invoke-WarehouseEpiRestoreSql $context postgres 'DROP DATABASE epi_restore_attack_fixture WITH (FORCE);'
    Prepare-LiveState
    $job = Prepare-Request
    Invoke-FixtureDriver
    $state = (Get-Content -LiteralPath (Join-Path $job.Directory 'status.json') -Raw | ConvertFrom-Json).State
    if ($state -ne 4) {
        $failurePath = Join-Path $FixtureRoot "Recovery\$($job.Id)\failure.json"
        $failure = if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath -Raw } else { 'No failure stage was recorded.' }
        throw "Full import must succeed. State=$state. $failure"
    }
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $FixtureRoot 'Config\service-settings.json') -Raw | ConvertFrom-Json).Security.PinLookupKey -ceq $backupKey) 'Import must install the paired key.'
    Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $FixtureRoot 'DataProtection-Keys\previous.txt'))) 'Previous login cookies must be invalidated.'
    Assert-Fixture ((Invoke-WarehouseEpiRestoreSql $context postgres "SELECT 1 FROM pg_database WHERE datname='warehouseEPI_before_$($job.Id)';") -eq '1') 'The previous database must be retained.'
    Assert-Fixture ((Invoke-WarehouseEpiRestoreSql $context postgres "SELECT has_database_privilege('warehouse_epi_app','warehouseEPI_before_$($job.Id)','CONNECT');") -ceq 'f') 'The app must not connect to the retained previous database.'
    Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $job.Directory 'pinlookupkey.json'))) 'Decrypted upload secrets must be removed.'
    Assert-Fixture ($global:RestoreFixture.Events.Contains('stop') -and $global:RestoreFixture.Events.Contains('start')) 'The service must be stopped and verified.'

    Prepare-LiveState
    $job = Prepare-Request; $global:RestoreFixture.FailStart = $true
    Invoke-FixtureDriver
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $job.Directory 'status.json') -Raw | ConvertFrom-Json).State -eq 5) 'A startup failure must roll back.'
    Assert-LiveState
    Assert-Fixture ((Invoke-WarehouseEpiRestoreSql $context postgres "SELECT has_database_privilege('warehouse_epi_app','warehouseEPI_failed_$($job.Id)','CONNECT');") -ceq 'f') 'The app must not connect to a failed imported database.'
    Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $FixtureRoot 'ManualBackups\.restore\maintenance.json'))) 'Successful rollback must release maintenance.'

    Prepare-LiveState
    $job = Prepare-Request '9999'
    Invoke-FixtureDriver
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $job.Directory 'status.json') -Raw | ConvertFrom-Json).State -eq 5) 'A wrong backup ADMIN PIN must reject import.'
    Assert-Fixture (-not $global:RestoreFixture.Events.Contains('stop')) 'Candidate validation must precede service shutdown.'
    Assert-LiveState

    Prepare-LiveState
    $job = Prepare-Request
    $queueRoot = Split-Path -Parent $job.Directory
    [IO.File]::Move((Join-Path $queueRoot 'pending.json'), (Join-Path $queueRoot 'maintenance.json'))
    $work = Join-Path $FixtureRoot "Recovery\$($job.Id)"; $previous = Join-Path $work 'previous'
    $null = New-Item -ItemType Directory -Path $previous -Force
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Config\service-settings.json') -Destination (Join-Path $previous 'service-settings.json')
    foreach ($name in @('Branding', 'WarehouseMapReferences', 'DataProtection-Keys')) { Copy-Item -LiteralPath (Join-Path $FixtureRoot $name) -Destination (Join-Path $previous $name) -Recurse }
    Write-WarehouseEpiRestoreJson (Join-Path $work 'journal.json') @{ SchemaVersion = 1; Id = $job.Id; Phase = 'Mutating' }
    $null = Invoke-WarehouseEpiRestoreSql $context postgres "ALTER DATABASE `"warehouseEPI`" RENAME TO `"warehouseEPI_before_$($job.Id)`";"
    Invoke-FixtureDriver
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $job.Directory 'status.json') -Raw | ConvertFrom-Json).State -eq 5) 'Interrupted rename must recover without replaying import.'
    Assert-LiveState
    Write-Output 'Private PostgreSQL import, startup rollback, wrong PIN, and interrupted rename: passed.'
}
finally {
    $databases = Invoke-WarehouseEpiRestoreSql $context postgres "SELECT datname FROM pg_database WHERE datname='warehouseEPI' OR datname ~ '^warehouseEPI_(before|failed|restore)_[a-f0-9]{32}$';"
    foreach ($database in $databases.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        $name = $database.Trim(); Assert-WarehouseEpiRestoreName $name
        $null = Invoke-WarehouseEpiRestoreSql $context postgres "DROP DATABASE `"$name`" WITH (FORCE);"
    }
    Remove-Variable RestoreFixture -Scope Global -ErrorAction SilentlyContinue
}
