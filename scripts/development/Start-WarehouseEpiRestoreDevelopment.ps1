#Requires -Version 7.4
[CmdletBinding()]
param([ValidateRange(1025, 65535)][int]$PostgreSqlPort = 55432, [ValidateRange(1025, 65535)][int]$WebPort = 5143)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Este inicio local requiere Windows y PostgreSQL 18.' }
if ($PostgreSqlPort -eq 5432 -or $PostgreSqlPort -eq $WebPort) { throw 'Use un puerto PostgreSQL diferente de 5432 y del puerto web.' }
. (Join-Path $PSScriptRoot 'WarehouseEpi.DevelopmentRestore.Common.ps1')
. (Join-Path $PSScriptRoot 'Invoke-WarehouseEpiDevelopmentRestore.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root = Join-Path $repository 'artifacts\local-restore'
$projectDirectory = Join-Path $repository 'src\WarehouseEPI.Web'
$null = Resolve-DevelopmentRestorePath $root $root
$null = New-Item -ItemType Directory -Path $root -Force
Set-DevelopmentRestorePrivateAcl $root
$launchLock = [IO.File]::Open((Resolve-DevelopmentRestorePath (Join-Path $root '.launch.lock') $root), 'OpenOrCreate', 'ReadWrite', 'None')
$appProcess = $null; $clusterStarted = $false; $context = $null
$bin = 'C:\Program Files\PostgreSQL\18\bin'
$cluster = Resolve-DevelopmentRestorePath (Join-Path $root 'postgres') $root
$configPath = Resolve-DevelopmentRestorePath (Join-Path $root 'config.json') $root
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$variables = @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'ServiceConfigPath', 'Development__RestoreConfigPath')
$previousEnvironment = @{}
foreach ($name in $variables) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function Stop-DevelopmentApplication {
    if ($script:appProcess -and -not $script:appProcess.HasExited) { $script:appProcess.Kill($true); $script:appProcess.WaitForExit() }
    if ($script:appProcess) { $script:appProcess.Dispose(); $script:appProcess = $null }
}
function Start-DevelopmentApplication {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $WebPort)
    try { $probe.Start() } catch { throw 'El puerto web de desarrollo está ocupado. Use otro puerto libre.' }
    finally { $probe.Stop() }
    $start = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $repository
    foreach ($argument in @($dll, "--contentRoot=$projectDirectory", "--urls=http://127.0.0.1:$WebPort", '--environment=Development')) { $start.ArgumentList.Add($argument) }
    $script:appProcess = [Diagnostics.Process]::Start($start)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        if ($script:appProcess.HasExited) { throw 'La aplicación de desarrollo no pudo iniciar.' }
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$WebPort/health/live" -TimeoutSec 2 -SkipHttpErrorCheck -NoProxy
            if ($response.StatusCode -eq 200 -and -not $script:appProcess.HasExited) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'La aplicación de desarrollo no respondió al iniciar.'
}

Push-Location $repository
try {
    foreach ($tool in @('initdb.exe', 'pg_ctl.exe', 'psql.exe', 'pg_restore.exe', 'pg_dump.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $tool) -PathType Leaf)) { throw 'Instale las herramientas PostgreSQL 18.' }
    }
    if (-not (Test-Path -LiteralPath $configPath)) {
        if (Test-Path -LiteralPath $cluster) { throw 'Existe un clúster sin configuración. Revise artifacts\local-restore.' }
        $ownerPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $appPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $connection = [Data.Common.DbConnectionStringBuilder]::new()
        $connection['Host'] = '127.0.0.1'; $connection['Port'] = $PostgreSqlPort
        $connection['Database'] = 'warehouse_epi_restore_dev'; $connection['Username'] = 'warehouse_epi_dev_app'; $connection['Password'] = $appPassword
        Write-WarehouseEpiRestoreJson $configPath @{
            ServiceConfigPath = ''
            ConnectionStrings = @{ Warehouse = $connection.get_ConnectionString() }
            Security = @{ PinLookupKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
            Development = @{ PostgreSqlPort = $PostgreSqlPort; UseEphemeralDataProtection = $true }
            Backups = @{ ManualDirectory = (Join-Path $root 'ManualBackups') }
            Branding = @{ StorageDirectory = (Join-Path $root 'Branding') }
            WarehouseMap = @{ ReferenceStorageDirectory = (Join-Path $root 'WarehouseMapReferences') }
            Observability = @{ LogDirectory = (Join-Path $root 'Logs') }
            AllowedHosts = 'localhost;127.0.0.1'
        }
        [IO.File]::WriteAllText((Resolve-DevelopmentRestorePath (Join-Path $root 'owner.pgpass') $root), "127.0.0.1:${PostgreSqlPort}:*:warehouse_epi_dev_owner:$ownerPassword`n")
        $passwordFile = Resolve-DevelopmentRestorePath (Join-Path $root '.init-password') $root
        try {
            [IO.File]::WriteAllText($passwordFile, $ownerPassword)
            & (Join-Path $bin 'initdb.exe') -D $cluster -U warehouse_epi_dev_owner --auth=scram-sha-256 --encoding=UTF8 --locale=C --pwfile=$passwordFile | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la instancia PostgreSQL de desarrollo.' }
        } finally { if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile -Force } }
    }
    $context = New-DevelopmentRestoreContext $root
    if ($context.Port -ne $PostgreSqlPort) { throw 'El clúster existente usa otro puerto. Use el mismo puerto al reiniciar.' }
    foreach ($folder in @('ManualBackups\.restore', 'Branding', 'WarehouseMapReferences', 'Recovery', 'Logs')) {
        $null = New-Item -ItemType Directory -Path (Resolve-DevelopmentRestorePath (Join-Path $root $folder) $root) -Force
    }
    if ((Invoke-DevelopmentClusterControl (Join-Path $bin 'pg_ctl.exe') @('-D', $cluster, 'status')) -ne 0) {
        $started = Invoke-DevelopmentClusterControl (Join-Path $bin 'pg_ctl.exe') @('-D', $cluster, '-l', (Join-Path $root 'postgres.log'), '-o', "-h 127.0.0.1 -p $PostgreSqlPort", '-w', 'start')
        if ($started -ne 0) { throw 'No se pudo iniciar PostgreSQL de desarrollo. Compruebe que su puerto esté libre.' }
    }
    Assert-DevelopmentRestoreCluster $context
    $clusterStarted = $true
    $exists = Invoke-WarehouseEpiRestoreSql $context postgres "SELECT 1 FROM pg_database WHERE datname='warehouse_epi_restore_dev';"
    if ($exists -ne '1') {
        $settingsConnection = [Data.Common.DbConnectionStringBuilder]::new()
        $settingsConnection.set_ConnectionString($context.Configuration.ConnectionStrings.Warehouse)
        $verifier = New-WarehouseEpiRestoreVerifier ([string]$settingsConnection['Password'])
        $roleExists = Invoke-WarehouseEpiRestoreSql $context postgres "SELECT 1 FROM pg_roles WHERE rolname='warehouse_epi_dev_app';"
        if ($roleExists -ne '1') {
            $null = Invoke-WarehouseEpiRestoreSql $context postgres "CREATE ROLE warehouse_epi_dev_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$verifier';"
        }
        $null = Invoke-WarehouseEpiRestoreSql $context postgres 'CREATE DATABASE warehouse_epi_restore_dev OWNER warehouse_epi_dev_app;'
    }
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Development', 'Process')
    [Environment]::SetEnvironmentVariable('ServiceConfigPath', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Development__RestoreConfigPath', $configPath, 'Process')
    & $dotnetPath build (Join-Path $projectDirectory 'WarehouseEPI.Web.csproj') --artifacts-path (Join-Path $root 'build') -p:UseAppHost=false -m:1 -nr:false --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la aplicación de desarrollo.' }
    $dll = Join-Path $root 'build\bin\WarehouseEPI.Web\debug\WarehouseEPI.Web.dll'
    if (-not (Test-Path -LiteralPath (Join-Path $root '.prepared'))) {
        Write-Host 'Primera ejecución: crea un ADMIN para esta base de pruebas. Sus datos se reemplazarán solo cuando confirmes una importación.'
        & $dotnetPath $dll "--contentRoot=$projectDirectory" --environment=Development --prepare-development-restore
        if ($LASTEXITCODE -ne 0) { throw 'No se completó la preparación de la base de desarrollo.' }
        [IO.File]::WriteAllText((Join-Path $root '.prepared'), 'Development')
    }
    $migrationIds = @(Get-ChildItem -LiteralPath (Join-Path $repository 'src\WarehouseEPI.Infrastructure\Persistence\Migrations') -Filter '*.Designer.cs' | ForEach-Object {
        $match = [regex]::Match([IO.File]::ReadAllText($_.FullName), '\[Migration\("(?<id>\d{14}_[A-Za-z0-9_]+)"\)\]')
        if ($match.Success) { $match.Groups['id'].Value }
    } | Sort-Object)
    Start-DevelopmentApplication
    Write-Host "Desarrollo con restauración: http://127.0.0.1:$WebPort"
    Write-Host "Base aislada: 127.0.0.1:$PostgreSqlPort / warehouse_epi_restore_dev. Detén con Ctrl+C."
    while (-not $appProcess.HasExited) {
        $context = New-DevelopmentRestoreContext $root
        Invoke-WarehouseEpiDevelopmentRestore $context $migrationIds { Stop-DevelopmentApplication } { Start-DevelopmentApplication }
        Start-Sleep -Seconds 1
    }
}
finally {
    Stop-DevelopmentApplication
    if ($clusterStarted) { $null = Invoke-DevelopmentClusterControl (Join-Path $bin 'pg_ctl.exe') @('-D', $cluster, '-w', '-m', 'fast', 'stop') }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    Pop-Location
    $launchLock.Dispose()
}
