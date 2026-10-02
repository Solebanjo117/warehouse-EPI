# Loaded only into an isolated test bundle; every machine mutation is intercepted.
function Test-Path {
    param([Alias('Path')][string]$LiteralPath, [string]$PathType)
    if ($LiteralPath.StartsWith('C:\ProgramData\WarehouseEPI', [StringComparison]::OrdinalIgnoreCase)) {
        return $global:SetupFixture.Files.ContainsKey($LiteralPath)
    }
    return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath -PathType $(if ($PathType) { $PathType } else { 'Any' })
}
function Get-Content {
    param([Alias('Path')][string]$LiteralPath, [switch]$Raw)
    if ($LiteralPath.StartsWith('C:\ProgramData\WarehouseEPI', [StringComparison]::OrdinalIgnoreCase)) {
        return $global:SetupFixture.Files[$LiteralPath]
    }
    return Microsoft.PowerShell.Management\Get-Content -LiteralPath $LiteralPath -Raw:$Raw
}
function Get-Item {
    param([string]$LiteralPath, [switch]$Force, [string]$ErrorAction)
    if ($LiteralPath.StartsWith('Cert:')) { return $global:SetupFixture.Certificate }
    if ($global:SetupFixture.Files.ContainsKey($LiteralPath)) {
        return [pscustomobject]@{ Attributes = [IO.FileAttributes]::Normal }
    }
    return Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force
}
function New-Item {
    param([string]$Path, [string]$ItemType, [switch]$Force)
    if (-not $Path.StartsWith('C:\ProgramData\WarehouseEPI', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation test attempted an unexpected filesystem mutation.'
    }
}
function Copy-Item {
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    if (-not $Destination.StartsWith('C:\ProgramData\WarehouseEPI\Maintenance\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation test attempted an unexpected copy.'
    }
    $global:SetupFixture.Files[$Destination] = Microsoft.PowerShell.Management\Get-Content -LiteralPath $LiteralPath -Raw
}
function icacls { $global:LASTEXITCODE = 0 }
function Get-NetTCPConnection { param([string]$State, [int[]]$LocalPort, [string]$ErrorAction) }
function Assert-WarehouseEpiAdministrator { }
function Get-WarehouseEpiService {
    if ($global:SetupFixture.ServiceInstalled) {
        return [pscustomobject]@{
            State = 'Running'
            PathName = '"C:\ProgramData\WarehouseEPI\Releases\0.0.0-fixture\WarehouseEPI.Web.exe"'
        }
    }
}
function curl.exe { $global:LASTEXITCODE = 0 }
function Get-Command {
    param([string]$Name, [string]$ErrorAction)
    if ($Name -eq 'curl.exe') { return [pscustomobject]@{ Name = 'curl.exe' } }
    throw 'The installation test attempted an unexpected command lookup.'
}
function Write-WarehouseEpiPrivateFile([string]$Path, [string]$Content) {
    if (-not $Path.StartsWith('C:\ProgramData\WarehouseEPI\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation test attempted an unexpected private write.'
    }
    $global:SetupFixture.Files[$Path] = $Content
    $global:SetupFixture.Events.Add('private-write')
}
function Read-WarehouseEpiHiddenValue([string]$Prompt) {
    $global:SetupFixture.Events.Add('secret-prompt')
    if ($Prompt -like '*PinLookupKey*') { return $global:SetupFixture.PinKey }
    if ($Prompt -like '*NIP*') { return '1234' }
    return 'test-password'
}
function Invoke-WarehouseEpiSetupSql([string]$PsqlPath, [string]$Database, [string]$Sql,
    [string]$PassFile, [string]$User = 'postgres') {
    $probe = $global:SetupFixture
    switch -Regex ($Sql) {
        '^SHOW server_version_num' { return '180004' }
        '^SELECT 1 FROM pg_database' { if ($probe.DatabaseExists) { return '1' }; return }
        '^CREATE DATABASE' { $probe.Events.Add('create-database'); $probe.DatabaseExists = $true; return }
        '^SELECT to_regclass' { return $(if ($probe.SchemaApplied) { 't' } else { 'f' }) }
        '^SELECT "MigrationId"' { return $probe.AppliedIds }
        '^SELECT 1 FROM users' { if ($probe.InvalidPinKey) { return }; return '1' }
        '^SELECT 1 FROM pg_roles' { return }
        '^SELECT count\(\*\) FROM users' { return [string]$probe.Users }
        'CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"' {
            $probe.Events.Add('schema'); $probe.SchemaApplied = $true
            $probe.AppliedIds = @('20261001000000_Fixture')
            return
        }
        'ALTER ROLE warehouse_epi_app PASSWORD' {
            if ($Sql -notmatch 'PASSWORD ''SCRAM-SHA-256\$') { throw 'Role password must use a SCRAM verifier.' }
            $probe.Events.Add('role'); return
        }
        default { throw 'The installation test attempted an unexpected SQL statement.' }
    }
}
