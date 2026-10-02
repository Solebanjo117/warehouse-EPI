#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$BundleDirectory = (Join-Path $PSScriptRoot '..\..'),
    [string]$ServerDnsName,
    [string]$CertificateThumbprint,
    [ValidateSet('New', 'Migrate')][string]$Mode,
    [string]$MigrationPackagePath,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Server.Common.ps1')
$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
$bundle = Test-WarehouseEpiServerBundle $bundleRoot
$releasePackage = Resolve-WarehouseEpiBundleFile $bundleRoot $bundle.releasePackage
$schemaPath = Resolve-WarehouseEpiBundleFile $bundleRoot $bundle.schemaFile
$release = Read-WarehouseEpiServerRelease $releasePackage
$bundleHash = (Get-FileHash -LiteralPath (Join-Path $bundleRoot 'server-bundle.json') -Algorithm SHA256).Hash
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
$psql = Join-Path $pgBin 'psql.exe'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'El asistente requiere Windows de 64 bits y PowerShell 7.4 o posterior.'
}
foreach ($tool in @('psql.exe', 'pg_dump.exe', 'pg_restore.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pgBin $tool) -PathType Leaf)) {
        throw 'Instale PostgreSQL 18 con sus herramientas en C:\Program Files\PostgreSQL\18 antes de continuar.'
    }
}
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'No se encontró curl.exe para verificar el arranque.' }
Write-Host "Paquete $($bundle.version) y herramientas locales verificados."
if ($CheckOnly) {
    Write-Host 'Comprobación terminada. No se solicitaron secretos ni se cambió el servidor.'
    return
}

. (Join-Path $bundleRoot 'scripts\release\WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
$statePath = 'C:\ProgramData\WarehouseEPI\Setup\server-setup.json'
$configPath = 'C:\ProgramData\WarehouseEPI\Config\service-settings.json'
$passPath = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass'
$state = $null
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.BundleHash -cne $bundleHash) { throw 'Hay una instalación incompleta de otro paquete. Termine ese procedimiento antes de cambiar de versión.' }
    $Mode = $state.Mode
    $ServerDnsName = $state.ServerDnsName
    $CertificateThumbprint = $state.CertificateThumbprint
    $MigrationPackagePath = $state.MigrationPackagePath
    Write-Host "Continuando la instalación pendiente de $($bundle.version)."
}
elseif (Test-Path -LiteralPath $configPath) {
    throw 'Ya existe una configuración ajena al asistente. Use la guía avanzada para conservarla.'
}
function Complete-WarehouseEpiServerSetup {
    Write-Host '[6/6] Activar respaldos, validación semanal e importación desde la aplicación.'
    $tasksRoot = Resolve-WarehouseEpiBundleFile 'C:\ProgramData\WarehouseEPI' 'Maintenance'
    $null = New-Item -ItemType Directory -Force -Path $tasksRoot
    & icacls $tasksRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger los scripts permanentes de respaldo.' }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'scripts\security') -File) {
        $target = Resolve-WarehouseEpiBundleFile $tasksRoot $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    & (Join-Path $bundleRoot 'scripts\security\Install-WarehouseEpiBackupTasks.ps1') -MaintenanceDirectory $tasksRoot -Force
    & (Join-Path $bundleRoot 'scripts\security\Install-WarehouseEpiRestoreTask.ps1') -Force
    $state.Phase = 'Installed'
    Write-WarehouseEpiPrivateFile $statePath ($state | ConvertTo-Json -Depth 4)
    Write-Host "Instalación terminada: https://$ServerDnsName/"
    Write-Host 'Verifique acceso desde una tablet, NIP, cámara e impresión. IT debe habilitar HTTPS solo desde la red autorizada.'
}
$installedService = Get-WarehouseEpiService
if ($null -ne $installedService) {
    $expectedExecutable = Join-Path $script:WarehouseEpiReleasesRoot "$($bundle.version)\WarehouseEPI.Web.exe"
    if ($null -eq $state -or $state.Phase -notin @('InstallingService', 'Installed') -or
        (Get-WarehouseEpiExecutableFromService $installedService) -ine $expectedExecutable -or $installedService.State -ne 'Running') {
        throw 'WarehouseEPI ya está instalado. Use la actualización de servicio; el asistente no reemplaza una instalación activa.'
    }
    & curl.exe --silent --fail --insecure --max-time 5 --header "Host: $ServerDnsName" 'https://127.0.0.1/health/live' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La instalación pendiente no responde al health local. Revise el servicio antes de completar las tareas.' }
    Complete-WarehouseEpiServerSetup
    return
}
if (Get-NetTCPConnection -State Listen -LocalPort 80,443 -ErrorAction SilentlyContinue) {
    throw 'Los puertos 80 o 443 están ocupados. Esta instalación usa HTTPS directo; libérelos o revise el alojamiento con IT.'
}
if ([string]::IsNullOrWhiteSpace($ServerDnsName)) { $ServerDnsName = Read-Host 'Nombre DNS del servidor, por ejemplo almacen.empresa.com' }
if ($ServerDnsName.Length -gt 253 -or $ServerDnsName -notmatch '^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$' -or
    @($ServerDnsName.Split('.') | Where-Object { $_ -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$' }).Count -gt 0) {
    throw 'Introduzca un nombre DNS válido, sin URL, puerto, comodines ni rutas.'
}
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Select-Object Subject, Thumbprint, NotAfter | Format-Table | Out-Host
    $CertificateThumbprint = Read-Host 'Thumbprint del certificado HTTPS instalado para ese nombre'
}
$CertificateThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($CertificateThumbprint -notmatch '^[A-F0-9]{40}$') { throw 'El thumbprint del certificado debe contener 40 caracteres hexadecimales.' }
$certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction Stop
$matchingName = @(Get-WarehouseEpiCertificateDnsNames $certificate | Where-Object {
    $dnsName = [string]$_
    $dnsName -ieq $ServerDnsName -or
    ($dnsName.StartsWith('*.') -and $ServerDnsName.EndsWith($dnsName.Substring(1), [StringComparison]::OrdinalIgnoreCase) -and
        $ServerDnsName.Split('.').Count -eq $dnsName.Split('.').Count)
}).Count -gt 0
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date) -or -not $matchingName) {
    throw 'El certificado debe estar vigente, incluir clave privada y corresponder al nombre DNS indicado.'
}
$eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
if ($eku.Count -gt 0 -and @($eku[0].EnhancedKeyUsages | Where-Object { $_.Value -in @('1.3.6.1.5.5.7.3.1', '2.5.29.37.0') }).Count -eq 0) {
    throw 'El certificado no está habilitado para autenticar un servidor HTTPS.'
}
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if ($null -eq $rsa) { throw 'Esta versión del servicio requiere un certificado con clave RSA.' }
$rsa.Dispose()
if ([string]::IsNullOrWhiteSpace($Mode)) {
    Write-Host '1. Instalar con una base nueva.  2. Migrar los datos del servidor anterior.'
    switch (Read-Host 'Seleccione 1 o 2') {
        '1' { $Mode = 'New' }
        '2' { $Mode = 'Migrate' }
        default { throw 'Seleccione 1 o 2 y vuelva a ejecutar el asistente.' }
    }
}
if ($Mode -eq 'New' -and -not [string]::IsNullOrWhiteSpace($MigrationPackagePath)) { throw 'Una instalación nueva no debe incluir un respaldo de migración.' }
if ($Mode -eq 'Migrate') {
    if ([string]::IsNullOrWhiteSpace($MigrationPackagePath)) { $MigrationPackagePath = Read-Host 'Ruta completa del ZIP de migración (su .sha256 debe estar al lado)' }
    $MigrationPackagePath = [IO.Path]::GetFullPath($MigrationPackagePath.Trim('"'))
    $null = & (Join-Path $bundleRoot 'scripts\security\Test-WarehouseEpiMigrationBackup.ps1') -PackagePath $MigrationPackagePath -RequireExternalHash
}

Write-Host '[1/6] Comprobar PostgreSQL y el destino.'
$adminPassword = Read-WarehouseEpiHiddenValue 'Contraseña de postgres en el servidor nuevo'
Write-WarehouseEpiPrivateFile $passPath (ConvertTo-WarehouseEpiPgPass $adminPassword)
$adminPassword = $null
function Invoke-SetupSql([string]$Database, [string]$Sql) {
    return Invoke-WarehouseEpiSetupSql $psql $Database $Sql $passPath
}
function Save-SetupState {
    Write-WarehouseEpiPrivateFile $statePath ($state | ConvertTo-Json -Depth 4)
}
$previousPgPass = $env:PGPASSFILE
$previousPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $null
    $serverVersion = [int](@(Invoke-SetupSql 'postgres' 'SHOW server_version_num;')[0])
    if ($serverVersion -lt 180000 -or $serverVersion -ge 190000) { throw 'Esta instalación requiere PostgreSQL 18.' }
    $exists = @(Invoke-SetupSql 'postgres' "SELECT 1 FROM pg_database WHERE datname = 'warehouseEPI';").Count -gt 0
    if ($null -eq $state) {
        if ($exists) { throw 'warehouseEPI ya existe. El asistente nunca reemplaza una base existente.' }
        $state = [ordered]@{
            BundleHash = $bundleHash; Mode = $Mode; ServerDnsName = $ServerDnsName
            CertificateThumbprint = $CertificateThumbprint; MigrationPackagePath = $MigrationPackagePath
            Phase = 'PreparingDatabase'
        }
        Save-SetupState
    }
    elseif ($exists -and $state.Phase -eq 'PreparingDatabase') {
        throw 'La creación o restauración se interrumpió antes de confirmarse. Revise ese destino con IT; no se eliminará ni reutilizará automáticamente.'
    }
    elseif (-not $exists -and $state.Phase -ne 'PreparingDatabase') {
        throw 'La base preparada anteriormente ya no existe. Revise el estado con IT antes de continuar.'
    }
    Write-Host '[2/6] Preparar la base de datos.'
    if (-not $exists) {
        if ($Mode -eq 'Migrate') {
            & (Join-Path $bundleRoot 'scripts\security\Restore-WarehouseEpiMigrationBackup.ps1') `
                -PackagePath $MigrationPackagePath -PgPassFile $passPath -Confirm:$false
        }
        else { $null = Invoke-SetupSql 'postgres' 'CREATE DATABASE "warehouseEPI";' }
        $state.Phase = 'DatabaseReady'
        Save-SetupState
    }
    $configuration = $null
    if (Test-Path -LiteralPath $configPath) {
        $configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
    }
    else {
        $pinKey = $null
        if ($Mode -eq 'Migrate') {
            $pairedKeyPath = Join-Path (Split-Path -Parent $MigrationPackagePath) 'pinlookupkey.json'
            if (Test-Path -LiteralPath $pairedKeyPath -PathType Leaf) {
                $pairedKey = Get-Content -LiteralPath $pairedKeyPath -Raw | ConvertFrom-Json
                $migrationHash = (Get-FileHash -LiteralPath $MigrationPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($pairedKey.SchemaVersion -ne 1 -or $pairedKey.MigrationPackageSha256 -cne $migrationHash) {
                    throw 'La PinLookupKey recuperada no corresponde a este ZIP. Use la pareja original.'
                }
                $pinKey = $pairedKey.PinLookupKey
                $pairedKey = $null
                Write-Host 'Se usará la PinLookupKey pareada del respaldo manual.'
            }
            else { $pinKey = Read-WarehouseEpiHiddenValue 'Security:PinLookupKey de la instalación anterior' }
        }
        else { $pinKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
        try { $keyBytes = [Convert]::FromBase64String($pinKey) }
        catch { throw 'La PinLookupKey debe ser Base64 válida.' }
        if ($keyBytes.Length -lt 32) { throw 'La PinLookupKey debe contener al menos 32 bytes.' }
        if ($Mode -eq 'Migrate') {
            $knownPin = Read-WarehouseEpiHiddenValue 'NIP conocido de un usuario de la instalación anterior (para verificar la clave)'
            if ($knownPin -notmatch '^\d{4,8}$') { throw 'El NIP de comprobación debe contener de 4 a 8 dígitos.' }
            $hmac = [Security.Cryptography.HMACSHA256]::new($keyBytes)
            try { $lookup = [Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($knownPin))).ToLowerInvariant() }
            finally { $hmac.Dispose(); $knownPin = $null }
            if (@(Invoke-SetupSql 'warehouseEPI' "SELECT 1 FROM users WHERE pin_lookup = '$lookup' LIMIT 1;").Count -eq 0) {
                throw 'La clave y el NIP no corresponden a los datos restaurados. Vuelva a ejecutar el asistente con los valores originales.'
            }
        }
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connection['Host'] = 'localhost'; $connection['Port'] = 5432; $connection['Database'] = 'warehouseEPI'
        $connection['Username'] = 'warehouse_epi_app'
        $connection['Password'] = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $configuration = [ordered]@{
            AllowedHosts = $ServerDnsName
            ConnectionStrings = @{ Warehouse = $connection.get_ConnectionString() }
            Security = @{ PinLookupKey = $pinKey; DataProtectionKeysPath = 'C:\ProgramData\WarehouseEPI\DataProtection-Keys'; ServerCertificateThumbprint = $CertificateThumbprint }
            Observability = @{ LogDirectory = 'C:\ProgramData\WarehouseEPI\Logs'; RetentionDays = 30; FileSizeLimitMegabytes = 50 }
            Branding = @{ StorageDirectory = 'C:\ProgramData\WarehouseEPI\Branding' }
            WarehouseMap = @{ ReferenceStorageDirectory = 'C:\ProgramData\WarehouseEPI\WarehouseMapReferences' }
        }
        Write-WarehouseEpiPrivateFile $configPath ($configuration | ConvertTo-Json -Depth 5)
        $pinKey = $null; [Array]::Clear($keyBytes)
    }
    Write-Host '[3/6] Respaldar y aplicar el esquema revisado incluido en el paquete.'
    $currentIds = @()
    if (@(Invoke-SetupSql 'warehouseEPI' 'SELECT to_regclass(''public."__EFMigrationsHistory"'') IS NOT NULL;')[0] -eq 't') {
        $currentIds = @(Invoke-SetupSql 'warehouseEPI' 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')
    }
    if (@($currentIds | Where-Object { $_ -cnotin $release.migrationIds }).Count -gt 0) {
        throw 'La base contiene migraciones ajenas o posteriores a la Release. No se aplicará el esquema.'
    }
    & (Join-Path $bundleRoot 'scripts\security\Initialize-WarehouseEpiBackupDirectory.ps1')
    $env:PGPASSFILE = $passPath
    & (Join-Path $bundleRoot 'scripts\security\Invoke-WarehouseEpiBackup.ps1') -PgPassFile $passPath
    $null = Invoke-SetupSql 'warehouseEPI' (Get-Content -LiteralPath $schemaPath -Raw)
    $appliedIds = @(Invoke-SetupSql 'warehouseEPI' 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')
    Assert-WarehouseEpiMigrationSet @($release.migrationIds) $appliedIds

    Write-Host '[4/6] Configurar permisos, claves y respaldos.'
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString($configuration.ConnectionStrings.Warehouse)
    if ($connection['Host'] -ne 'localhost' -or [string]$connection['Port'] -ne '5432' -or
        $connection['Database'] -cne 'warehouseEPI' -or $connection['Username'] -cne 'warehouse_epi_app' -or
        $configuration.AllowedHosts -cne $ServerDnsName -or $configuration.Security.ServerCertificateThumbprint -cne $CertificateThumbprint) {
        throw 'La configuración pendiente no corresponde a este destino. Revísela con IT.'
    }
    $unsafeRole = @(Invoke-SetupSql 'warehouseEPI' "SELECT 1 FROM pg_roles WHERE rolname = 'warehouse_epi_app' AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls);")
    if ($unsafeRole.Count -gt 0) { throw 'El rol warehouse_epi_app existente tiene privilegios administrativos; IT debe corregirlos antes de instalar.' }
    $verifier = New-WarehouseEpiScramVerifier ([string]$connection['Password'])
    $roleSql = Get-Content -LiteralPath (Join-Path $bundleRoot 'scripts\security\provision-postgresql-role.sql') -Raw
    $roleSql = $roleSql.Replace('\password warehouse_epi_app', "ALTER ROLE warehouse_epi_app PASSWORD '$verifier';")
    $null = Invoke-SetupSql 'warehouseEPI' $roleSql
    $users = [int](@(Invoke-SetupSql 'warehouseEPI' 'SELECT count(*) FROM users;')[0])
    & (Join-Path $bundleRoot 'scripts\security\Initialize-DataProtectionKeys.ps1')
    & (Join-Path $bundleRoot 'scripts\security\Initialize-ObservabilityLogs.ps1')
    & (Join-Path $bundleRoot 'scripts\security\Invoke-WarehouseEpiBackup.ps1') -PgPassFile $passPath
    & (Join-Path $bundleRoot 'scripts\security\Invoke-WarehouseEpiRecoveryValidation.ps1') -PgPassFile $passPath
    $state.Phase = 'InstallingService'; Save-SetupState

    Write-Host '[5/6] Instalar el servicio y verificar HTTPS local.'
    & (Join-Path $bundleRoot 'scripts\release\Install-WarehouseEpiService.ps1') -PackagePath $releasePackage -CreateAdministrator:($users -eq 0)
    Complete-WarehouseEpiServerSetup
}
catch {
    Write-Host 'El asistente se detuvo. Conserva los datos y su estado para revisión; no cambie de paquete ni borre la base para reintentar.'
    throw
}
finally { $env:PGPASSFILE = $previousPgPass; $env:PGPASSWORD = $previousPgPassword }
