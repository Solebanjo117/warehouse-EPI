#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SchemaPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\server')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Server.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$release = Read-WarehouseEpiServerRelease $PackagePath
$schema = Get-Content -LiteralPath $SchemaPath -Raw
$sqlIds = @(Get-WarehouseEpiSqlMigrationIds $schema)
Assert-WarehouseEpiMigrationSet @($release.migrationIds) $sqlIds
if ($schema -notmatch '(?is)CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+"__EFMigrationsHistory"' -or
    $schema -notmatch '(?is)IF\s+NOT\s+EXISTS\s*\(SELECT.+?"MigrationId"') {
    throw 'Incluya el SQL completo idempotente, generado y revisado para esta Release.'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$name = "WarehouseEPI-$($release.version)-server"
$staging = Resolve-WarehouseEpiBundleFile $outputRoot $name
$zipPath = "$staging.zip"
if ((Test-Path -LiteralPath $staging) -or (Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath "$zipPath.sha256")) {
    throw 'Ya existe un paquete de servidor para esa versión en el directorio de salida.'
}
$null = New-Item -ItemType Directory -Force -Path $staging
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $staging 'release')
    $releaseName = Split-Path -Leaf $PackagePath
    Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $staging "release\$releaseName")
    Copy-Item -LiteralPath "$PackagePath.sha256" -Destination (Join-Path $staging "release\$releaseName.sha256")
    [IO.File]::WriteAllText((Join-Path $staging 'schema.sql'), $schema, [Text.UTF8Encoding]::new($false))
    foreach ($folder in @('release', 'security', 'server')) {
        $target = Join-Path $staging "scripts\$folder"
        $null = New-Item -ItemType Directory -Force -Path $target
        $source = Join-Path $repositoryRoot "scripts\$folder"
        foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in @('.ps1', '.sql') }) {
            if ($file.Name -in @('New-WarehouseEpiServerBundle.ps1', 'Publish-WarehouseEpiRelease.ps1')) { continue }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name)
        }
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALLATION_SERVER.md') -Destination (Join-Path $staging 'LEEME.md')
    $launcher = @'
#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ServerDnsName,
    [string]$CertificateThumbprint,
    [ValidateSet('New', 'Migrate')][string]$Mode,
    [string]$MigrationPackagePath,
    [switch]$CheckOnly
)
& (Join-Path $PSScriptRoot 'scripts\server\Install-WarehouseEpiServer.ps1') -BundleDirectory $PSScriptRoot @PSBoundParameters
'@
    [IO.File]::WriteAllText((Join-Path $staging 'Install.ps1'), $launcher, [Text.UTF8Encoding]::new($false))
    $files = @(Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1; packageType = 'WarehouseEPI-Server'; version = $release.version
        gitCommit = $release.gitCommit; releasePackage = "release/$releaseName"; schemaFile = 'schema.sql'
        files = $files
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'server-bundle.json') -Encoding utf8NoBOM
    $null = Test-WarehouseEpiServerBundle $staging
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
    Write-Host "Paquete para IT: $zipPath"
    Write-Host 'Entregue también el .sha256. Los datos y secretos se transfieren por separado.'
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $safeStaging = Resolve-WarehouseEpiBundleFile $outputRoot $name
        $tree = @(Get-ChildItem -LiteralPath $safeStaging -Recurse -Force)
        if (@($tree | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
            throw 'No se eliminará un directorio temporal que contiene enlaces.'
        }
        Remove-Item -LiteralPath $safeStaging -Recurse -Force
    }
}
