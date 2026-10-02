#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot 'scripts\server\WarehouseEpi.Server.Common.ps1')
$fixtureRoot = Join-Path $repositoryRoot "artifacts\server-tests\$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Force -Path $fixtureRoot
$script:checks = 0
function Assert-Condition([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Fallo: $Description" }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Description) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Condition $rejected $Description
}
function Set-FixtureHash([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$Path.sha256" -Value "$hash  $(Split-Path -Leaf $Path)" -Encoding ascii
}
function Set-BundleFileHash([string]$Root, [string]$RelativePath) {
    $manifestPath = Join-Path $Root 'server-bundle.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    ($manifest.files | Where-Object { $_.path -ceq $RelativePath }).sha256 = (Get-FileHash -LiteralPath (Join-Path $Root $RelativePath) -Algorithm SHA256).Hash
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}

try {
    foreach ($scriptPath in @('scripts/server/WarehouseEpi.Server.Common.ps1',
        'scripts/server/New-WarehouseEpiServerBundle.ps1', 'scripts/server/Install-WarehouseEpiServer.ps1',
        'scripts/release/Publish-WarehouseEpiRelease.ps1', 'scripts/release/Install-WarehouseEpiService.ps1',
        'scripts/security/Install-WarehouseEpiBackupTasks.ps1')) {
        $tokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $scriptPath), [ref]$tokens, [ref]$parseErrors)
        Assert-Condition ($parseErrors.Count -eq 0) "Sintaxis de $scriptPath"
    }
    foreach ($unsafePath in @('../outside.txt', 'x/../../outside.txt', 'C:\outside.txt', 'file:stream', '/outside.txt')) {
        Assert-Rejected { Resolve-WarehouseEpiBundleFile $fixtureRoot $unsafePath } "Rechazar ruta $unsafePath"
    }
    $migrationId = '20261001000000_Fixture'
    $sql = @'
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" text NOT NULL, "ProductVersion" text NOT NULL);
DO $EF$
BEGIN
IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261001000000_Fixture') THEN
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20261001000000_Fixture', '10.0.10');
END IF;
END $EF$;
'@
    Assert-Condition ((@(Get-WarehouseEpiSqlMigrationIds $sql) -join ',') -ceq $migrationId) 'Leer migraciones del SQL PostgreSQL'
    Assert-Rejected { Assert-WarehouseEpiMigrationSet @($migrationId) @('20261002000000_Other') } 'Rechazar un esquema de otra versión'
    Assert-Rejected { Assert-WarehouseEpiMigrationSet @($migrationId) @($migrationId.ToLowerInvariant()) } 'Comparar identidades de migraciones respetando mayúsculas'
    Assert-Rejected { Assert-WarehouseEpiMigrationSet @($migrationId) @() } 'Rechazar una base sin migraciones aplicadas'
    $source = Join-Path $fixtureRoot 'release-source'
    $null = New-Item -ItemType Directory -Path $source
    [IO.File]::WriteAllText((Join-Path $source 'WarehouseEPI.Web.exe'), 'TEST FIXTURE: NOT AN APPLICATION')
    $releaseManifest = [ordered]@{
        version = '0.0.0-fixture'; runtime = 'win-x64'; selfContained = $true
        gitCommit = '0000000000000000000000000000000000000000'; migrationIds = @($migrationId)
        files = @(@{ path = 'WarehouseEPI.Web.exe'; sha256 = (Get-FileHash -LiteralPath (Join-Path $source 'WarehouseEPI.Web.exe')).Hash })
    }
    $releaseManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $source 'release-manifest.json') -Encoding utf8NoBOM
    $releasePath = Join-Path $fixtureRoot 'fixture.zip'
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $releasePath
    Set-FixtureHash $releasePath
    Assert-Condition ((Read-WarehouseEpiServerRelease $releasePath).version -ceq '0.0.0-fixture') 'Verificar la Release antes de empaquetar'
    $legacy = Join-Path $fixtureRoot 'legacy.zip'
    $savedManifest = Get-Content -LiteralPath (Join-Path $source 'release-manifest.json') -Raw
    $releaseManifest.Remove('migrationIds')
    $releaseManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $source 'release-manifest.json') -Encoding utf8NoBOM
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $legacy
    Set-FixtureHash $legacy
    Assert-Rejected { Read-WarehouseEpiServerRelease $legacy } 'Rechazar Releases antiguas sin metadata de esquema'
    [IO.File]::WriteAllText((Join-Path $source 'release-manifest.json'), $savedManifest)
    $schemaPath = Join-Path $fixtureRoot 'schema.sql'
    [IO.File]::WriteAllText($schemaPath, $sql)
    $output = Join-Path $fixtureRoot 'packages'
    $publisher = Join-Path $repositoryRoot 'scripts\server\New-WarehouseEpiServerBundle.ps1'
    & $publisher -PackagePath $releasePath -SchemaPath $schemaPath -OutputDirectory $output
    $bundleZip = Join-Path $output 'WarehouseEPI-0.0.0-fixture-server.zip'
    Assert-Condition (Test-Path -LiteralPath "$bundleZip.sha256") 'Emitir ZIP y hash externo'
    $bundleRoot = Join-Path $fixtureRoot 'extracted'
    Expand-Archive -LiteralPath $bundleZip -DestinationPath $bundleRoot
    Assert-Condition ((Test-WarehouseEpiServerBundle $bundleRoot).version -ceq '0.0.0-fixture') 'Verificar el paquete extraído completo'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $bundleRoot 'src'))) 'Paquete sin código fuente ni herramientas de desarrollo'
    $existingSetup = 'C:\ProgramData\WarehouseEPI\Setup\server-setup.json'
    $before = if (Test-Path -LiteralPath $existingSetup) { (Get-FileHash -LiteralPath $existingSetup).Hash } else { 'missing' }
    & (Join-Path $bundleRoot 'Install.ps1') -CheckOnly
    $after = if (Test-Path -LiteralPath $existingSetup) { (Get-FileHash -LiteralPath $existingSetup).Hash } else { 'missing' }
    Assert-Condition ($before -ceq $after) 'CheckOnly no cambia el estado ni ejecuta el falso binario'
    $originalSql = Get-Content -LiteralPath (Join-Path $bundleRoot 'schema.sql') -Raw
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'schema.sql'), 'tampered')
    Assert-Rejected { Test-WarehouseEpiServerBundle $bundleRoot } 'Rechazar SQL alterado'
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'schema.sql'), $originalSql.Replace($migrationId, '20261002000000_Other'))
    Set-BundleFileHash $bundleRoot 'schema.sql'
    Assert-Rejected { Test-WarehouseEpiServerBundle $bundleRoot } 'Rechazar SQL de otra Release aun con hash válido'
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'schema.sql'), $originalSql)
    Set-BundleFileHash $bundleRoot 'schema.sql'
    Remove-Item -LiteralPath (Join-Path $bundleRoot 'scripts\security\Invoke-WarehouseEpiBackup.ps1')
    Assert-Rejected { Test-WarehouseEpiServerBundle $bundleRoot } 'Rechazar ausencia de scripts de recuperación'
    $nonIdempotent = Join-Path $fixtureRoot 'non-idempotent.sql'
    Set-Content -LiteralPath $nonIdempotent -Value "INSERT INTO `"__EFMigrationsHistory`" (`"MigrationId`", `"ProductVersion`") VALUES ('$migrationId', '10.0.10');"
    Assert-Rejected { & $publisher -PackagePath $releasePath -SchemaPath $nonIdempotent -OutputDirectory (Join-Path $fixtureRoot 'invalid') } 'Rechazar SQL no idempotente'
    Assert-Condition ((ConvertTo-WarehouseEpiPgPass 'a:b\c') -ceq 'localhost:5432:*:postgres:a\:b\\c') 'Escapar dos puntos y barras en pgpass'
    Assert-Rejected { ConvertTo-WarehouseEpiPgPass "a`nb" } 'Rechazar inyección de una segunda credencial pgpass'
    $password = [Convert]::ToBase64String([byte[]](1..32))
    $verifier = New-WarehouseEpiScramVerifier $password
    Assert-Condition ($verifier -match '^SCRAM-SHA-256\$4096:([^$]+)\$([^:]+):(.+)$') 'Formato SCRAM compatible con PostgreSQL'
    $salt = [Convert]::FromBase64String($Matches[1]); $stored = $Matches[2]; $server = $Matches[3]
    $derived = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 4096, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $mac = [Security.Cryptography.HMACSHA256]::new($derived)
    try {
        Assert-Condition ([Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Client Key')))) -ceq $stored) 'Verificador SCRAM reconoce la contraseña generada'
        Assert-Condition ([Convert]::ToBase64String($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Server Key'))) -ceq $server) 'Clave de servidor SCRAM correcta'
    }
    finally { $mac.Dispose() }
    $fakePsql = Join-Path $fixtureRoot 'psql-fixture.ps1'
    @'
$global:SetupSqlProbe = @{ Input = @($input) -join "`n"; Arguments = $args; PassFile = $env:PGPASSFILE; Password = $env:PGPASSWORD }
$global:LASTEXITCODE = if ($global:SetupSqlShouldFail) { 1 } else { 0 }
if (-not $global:SetupSqlShouldFail) { 'ok' }
'@ | Set-Content -LiteralPath $fakePsql -Encoding utf8NoBOM
    $savedPass = $env:PGPASSFILE; $savedPassword = $env:PGPASSWORD
    try {
        $env:PGPASSFILE = 'previous-fixture'; $env:PGPASSWORD = 'previous-test-password'
        $global:SetupSqlShouldFail = $false
        $result = @(Invoke-WarehouseEpiSetupSql $fakePsql 'postgres' 'SELECT fixture;' 'test.pgpass')
        Assert-Condition ($result[0] -ceq 'ok') 'Enviar el SQL por stdin'
        Assert-Condition ($global:SetupSqlProbe.Input -ceq 'SELECT fixture;') 'Transmitir el SQL exacto'
        Assert-Condition ([string]::IsNullOrEmpty($global:SetupSqlProbe.Password) -and $global:SetupSqlProbe.PassFile -ceq 'test.pgpass') 'Credenciales tomadas del pgpass protegido'
        Assert-Condition (($global:SetupSqlProbe.Arguments -join ' ') -notmatch 'SELECT|previous-test-password') 'No poner SQL ni secretos en los argumentos'
        Assert-Condition ($env:PGPASSFILE -ceq 'previous-fixture' -and $env:PGPASSWORD -ceq 'previous-test-password') 'Restaurar variables de entorno'
        $global:SetupSqlShouldFail = $true
        Assert-Rejected { Invoke-WarehouseEpiSetupSql $fakePsql 'postgres' 'SELECT fixture;' 'test.pgpass' } 'Detener la instalación ante error PostgreSQL'
        Assert-Condition ($env:PGPASSFILE -ceq 'previous-fixture' -and $env:PGPASSWORD -ceq 'previous-test-password') 'Restaurar entorno también ante fallo'
    }
    finally { $env:PGPASSFILE = $savedPass; $env:PGPASSWORD = $savedPassword; Remove-Variable SetupSqlProbe,SetupSqlShouldFail -Scope Global -ErrorAction SilentlyContinue }
    # Exercise the complete wizard with an in-memory certificate/database/service.
    $flowRoot = Join-Path $fixtureRoot 'flow-bundle'
    Expand-Archive -LiteralPath $bundleZip -DestinationPath $flowRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ServerInstallation.Mock.ps1') -Destination (Join-Path $flowRoot 'scripts\server\Installation.Mock.ps1')
    foreach ($common in @('scripts/server/WarehouseEpi.Server.Common.ps1', 'scripts/release/WarehouseEpi.Release.Common.ps1')) {
        Add-Content -LiteralPath (Join-Path $flowRoot $common) -Value "`n. (Join-Path '$flowRoot' 'scripts\server\Installation.Mock.ps1')"
    }
    $stub = @'
param([string]$PackagePath, [string]$PgPassFile, [string]$MaintenanceDirectory, [switch]$CreateAdministrator, [switch]$Force, [switch]$Confirm, [switch]$RequireExternalHash)
switch ($MyInvocation.MyCommand.Name) {
    'Invoke-WarehouseEpiBackup.ps1' { $global:SetupFixture.Events.Add('backup') }
    'Invoke-WarehouseEpiRecoveryValidation.ps1' { $global:SetupFixture.Events.Add('restore-validation') }
    'Test-WarehouseEpiMigrationBackup.ps1' { $global:SetupFixture.Events.Add('migration-hash-check') }
    'Restore-WarehouseEpiMigrationBackup.ps1' {
        $global:SetupFixture.Events.Add('restore-migration'); $global:SetupFixture.DatabaseExists = $true
    }
    'Install-WarehouseEpiService.ps1' {
        if ($global:SetupFixture.FailService) { $global:SetupFixture.FailService = $false; throw 'Test service installation failure.' }
        $global:SetupFixture.Events.Add('install-service'); $global:SetupFixture.ServiceInstalled = $true
        if ($CreateAdministrator) { $global:SetupFixture.Events.Add('create-admin'); $global:SetupFixture.Users = 1 }
    }
    'Install-WarehouseEpiBackupTasks.ps1' {
        if ($MaintenanceDirectory -cne 'C:\ProgramData\WarehouseEPI\Maintenance') { throw 'Tasks must use their persistent directory.' }
        $global:SetupFixture.Events.Add('schedule-backups')
    }
    'Install-WarehouseEpiRestoreTask.ps1' { $global:SetupFixture.Events.Add('schedule-restores') }
}
$global:LASTEXITCODE = 0
'@
    foreach ($relative in @('scripts/release/Install-WarehouseEpiService.ps1',
        'scripts/security/Initialize-WarehouseEpiBackupDirectory.ps1', 'scripts/security/Invoke-WarehouseEpiBackup.ps1',
        'scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1', 'scripts/security/Initialize-DataProtectionKeys.ps1',
        'scripts/security/Initialize-ObservabilityLogs.ps1', 'scripts/security/Install-WarehouseEpiBackupTasks.ps1', 'scripts/security/Install-WarehouseEpiRestoreTask.ps1',
        'scripts/security/Test-WarehouseEpiMigrationBackup.ps1', 'scripts/security/Restore-WarehouseEpiMigrationBackup.ps1')) {
        [IO.File]::WriteAllText((Join-Path $flowRoot $relative), $stub)
    }
    $flowManifest = Get-Content -LiteralPath (Join-Path $flowRoot 'server-bundle.json') -Raw | ConvertFrom-Json
    $flowManifest.files = @(Get-ChildItem -LiteralPath $flowRoot -Recurse -File |
        Where-Object { $_.Name -ne 'server-bundle.json' } | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($flowRoot, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
    $flowManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $flowRoot 'server-bundle.json') -Encoding utf8NoBOM
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=fixture.example.com', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('fixture.example.com')
    $request.CertificateExtensions.Add($san.Build())
    $testCertificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(2))
    function Reset-SetupFixture([bool]$Exists = $false, [int]$Users = 0) {
        $global:SetupFixture = @{
            Files = @{}; Events = [Collections.Generic.List[string]]::new(); Certificate = $testCertificate
            DatabaseExists = $Exists; SchemaApplied = $false; AppliedIds = @(); Users = $Users
            ServiceInstalled = $false; FailService = $false; InvalidPinKey = $false
            PinKey = [Convert]::ToBase64String([byte[]](1..32))
        }
    }
    $runFlow = { & (Join-Path $flowRoot 'Install.ps1') -ServerDnsName 'fixture.example.com' -CertificateThumbprint $testCertificate.Thumbprint -Mode New }
    try {
        Reset-SetupFixture
        & $runFlow
        Assert-Condition ($global:SetupFixture.ServiceInstalled) 'Instalación nueva completa con servicio simulado'
        Assert-Condition ($global:SetupFixture.Events.Contains('create-admin')) 'Crear primer administrador solo para una base vacía'
        Assert-Condition ($global:SetupFixture.Events.IndexOf('backup') -lt $global:SetupFixture.Events.IndexOf('schema')) 'Respaldar antes de aplicar el esquema'
        Assert-Condition ($global:SetupFixture.Events.IndexOf('restore-validation') -lt $global:SetupFixture.Events.IndexOf('install-service')) 'Probar restauración antes de activar el servicio'
        Assert-Condition ($global:SetupFixture.Events.Contains('schedule-backups')) 'Programar mantenimiento después de instalar'
        Assert-Condition ($global:SetupFixture.Events.Contains('schedule-restores')) 'Habilitar importación desde la aplicación después de instalar'
        Assert-Condition ((($global:SetupFixture.Files['C:\ProgramData\WarehouseEPI\Setup\server-setup.json'] | ConvertFrom-Json).Phase) -ceq 'Installed') 'Registrar el final de instalación'
        $beforeCount = @($global:SetupFixture.Events | Where-Object { $_ -eq 'schema' }).Count
        & $runFlow
        Assert-Condition (@($global:SetupFixture.Events | Where-Object { $_ -eq 'schema' }).Count -eq $beforeCount) 'Reintentar la instalación terminada sin volver a tocar el esquema'
        Reset-SetupFixture -Exists $true
        Assert-Rejected $runFlow 'Bloquear una base existente ajena al asistente'
        Assert-Condition (-not $global:SetupFixture.Events.Contains('schema') -and -not $global:SetupFixture.Events.Contains('install-service')) 'No modificar una base ajena ni instalar el servicio'
        Reset-SetupFixture
        $global:SetupFixture.FailService = $true
        Assert-Rejected $runFlow 'Conservar estado si falla la creación del servicio'
        & $runFlow
        Assert-Condition ($global:SetupFixture.ServiceInstalled -and @($global:SetupFixture.Events | Where-Object { $_ -eq 'create-database' }).Count -eq 1) 'Continuar sin recrear una base ya preparada'
        Reset-SetupFixture -Users 1
        & (Join-Path $flowRoot 'Install.ps1') -ServerDnsName 'fixture.example.com' -CertificateThumbprint $testCertificate.Thumbprint -Mode Migrate -MigrationPackagePath (Join-Path $fixtureRoot 'migration.zip')
        Assert-Condition ($global:SetupFixture.Events.Contains('restore-migration') -and -not $global:SetupFixture.Events.Contains('create-admin')) 'Migrar usuarios sin crear otro administrador'
        $migrated = $global:SetupFixture.Files['C:\ProgramData\WarehouseEPI\Config\service-settings.json'] | ConvertFrom-Json
        Assert-Condition ($migrated.Security.PinLookupKey -ceq $global:SetupFixture.PinKey) 'Conservar la clave NIP original'
        $migrationFixture = Join-Path $fixtureRoot 'migration.zip'
        [IO.File]::WriteAllText($migrationFixture, 'migration fixture')
        $pairedKeyFixture = Join-Path $fixtureRoot 'pinlookupkey.json'
        $migrationFixtureHash = (Get-FileHash -LiteralPath $migrationFixture).Hash.ToLowerInvariant()
        Reset-SetupFixture -Users 1
        @{ SchemaVersion = 1; MigrationPackageSha256 = $migrationFixtureHash; PinLookupKey = $global:SetupFixture.PinKey } |
            ConvertTo-Json | Set-Content -LiteralPath $pairedKeyFixture
        & (Join-Path $flowRoot 'Install.ps1') -ServerDnsName 'fixture.example.com' -CertificateThumbprint $testCertificate.Thumbprint -Mode Migrate -MigrationPackagePath $migrationFixture
        $pairedConfiguration = $global:SetupFixture.Files['C:\ProgramData\WarehouseEPI\Config\service-settings.json'] | ConvertFrom-Json
        Assert-Condition ($pairedConfiguration.Security.PinLookupKey -ceq $global:SetupFixture.PinKey) 'Usar la clave pareada recuperada del respaldo manual'
        Assert-Condition (@($global:SetupFixture.Events | Where-Object { $_ -eq 'secret-prompt' }).Count -eq 2) 'No pedir transcribir la clave pareada; solo postgres y NIP de comprobación'
        Reset-SetupFixture -Users 1
        @{ SchemaVersion = 1; MigrationPackageSha256 = ('0' * 64); PinLookupKey = $global:SetupFixture.PinKey } |
            ConvertTo-Json | Set-Content -LiteralPath $pairedKeyFixture
        Assert-Rejected {
            & (Join-Path $flowRoot 'Install.ps1') -ServerDnsName 'fixture.example.com' -CertificateThumbprint $testCertificate.Thumbprint -Mode Migrate -MigrationPackagePath $migrationFixture
        } 'Rechazar una clave recuperada de otro paquete'
        Assert-Condition (-not $global:SetupFixture.Events.Contains('schema')) 'No aplicar el esquema con una clave de otro respaldo'
        Remove-Item -LiteralPath $pairedKeyFixture
        Reset-SetupFixture -Users 1
        $global:SetupFixture.InvalidPinKey = $true
        Assert-Rejected {
            & (Join-Path $flowRoot 'Install.ps1') -ServerDnsName 'fixture.example.com' -CertificateThumbprint $testCertificate.Thumbprint -Mode Migrate -MigrationPackagePath (Join-Path $fixtureRoot 'migration.zip')
        } 'Detener una migración si la clave NIP es incorrecta'
        Assert-Condition (-not $global:SetupFixture.Events.Contains('schema')) 'No aplicar esquema antes de verificar la clave original'
    }
    finally { $testCertificate.Dispose(); $rsa.Dispose(); Remove-Variable SetupFixture -Scope Global -ErrorAction SilentlyContinue }
    Write-Host "$script:checks comprobaciones aprobadas. No se modificaron servicios, certificados ni bases reales."
}
finally {
    $safeRoot = Resolve-WarehouseEpiBundleFile (Join-Path $repositoryRoot 'artifacts\server-tests') (Split-Path -Leaf $fixtureRoot)
    Remove-Item -LiteralPath $safeRoot -Recurse -Force
}
