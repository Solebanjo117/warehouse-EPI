[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$ProjectPath = 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
if ($null -ne (Get-WarehouseEpiService)) { throw 'El servicio WarehouseEPI ya existe; use el script de actualización.' }
Assert-WarehouseEpiValidatedBackup
Test-WarehouseEpiPackageHash $PackagePath
if (-not (Test-Path -LiteralPath $script:WarehouseEpiConfigPath)) {
    & (Join-Path $PSScriptRoot 'Initialize-WarehouseEpiServiceConfiguration.ps1') -ProjectPath $ProjectPath
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible migrar la configuración del servicio.' }
}

$release = Expand-WarehouseEpiReleasePackage $PackagePath
try { Invoke-WarehouseEpiPreflight $release.Executable }
catch {
    Remove-WarehouseEpiInactiveRelease $release.Path
    throw
}
$created = $false
try {
    $commandLine = "`"$($release.Executable)`" --environment Production --contentRoot=`"$($release.Path)`" --ServiceConfigPath=`"$script:WarehouseEpiConfigPath`""
    & sc.exe create $script:WarehouseEpiServiceName 'binPath=' $commandLine 'start=' 'delayed-auto' 'obj=' $script:WarehouseEpiServiceIdentity | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible crear el servicio Windows.' }
    $created = $true
    & sc.exe description $script:WarehouseEpiServiceName 'Warehouse EPI - inventario local' | Out-Null
    & sc.exe failure $script:WarehouseEpiServiceName 'reset=' 86400 'actions=' 'restart/60000/restart/60000/none/0' | Out-Null
    Grant-WarehouseEpiServiceResources $release.Path
    Start-WarehouseEpiServiceAndVerify
    Write-Host "WarehouseEPI quedó activo en la versión $($release.Version)."
}
catch {
    if ($created) {
        Stop-Service -Name $script:WarehouseEpiServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $script:WarehouseEpiServiceName | Out-Null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        while ($null -ne (Get-WarehouseEpiService) -and [DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }
    if ((Test-Path -LiteralPath $release.Path) -and $null -eq (Get-WarehouseEpiService)) {
        Remove-WarehouseEpiInactiveRelease $release.Path
    }
    throw
}
