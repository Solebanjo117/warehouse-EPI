[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
$service = Get-WarehouseEpiService
$previousExecutable = Get-WarehouseEpiExecutableFromService $service
$previousDirectory = Test-WarehouseEpiReleaseDirectory (Split-Path -Parent $previousExecutable)
Assert-WarehouseEpiValidatedBackup
Test-WarehouseEpiPackageHash $PackagePath
$release = Expand-WarehouseEpiReleasePackage $PackagePath
Grant-WarehouseEpiServiceResources $release.Path
try { Invoke-WarehouseEpiPreflight $release.Executable }
catch {
    Remove-WarehouseEpiInactiveRelease $release.Path
    throw
}

Stop-WarehouseEpiServiceSafely
try {
    Set-WarehouseEpiServiceBinary $release.Executable
    Start-WarehouseEpiServiceAndVerify
}
catch {
    Stop-WarehouseEpiServiceSafely
    Set-WarehouseEpiServiceBinary $previousExecutable
    Start-WarehouseEpiServiceAndVerify
    Remove-WarehouseEpiInactiveRelease $release.Path
    throw 'La actualización falló y se restauró automáticamente la versión anterior.'
}
Remove-ExpiredWarehouseEpiReleases 2
Write-Host "WarehouseEPI fue actualizado a la versión $($release.Version)."
