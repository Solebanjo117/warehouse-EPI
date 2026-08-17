[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
Assert-WarehouseEpiVersion $Version
$service = Get-WarehouseEpiService
$previousExecutable = Get-WarehouseEpiExecutableFromService $service
$targetDirectory = Test-WarehouseEpiReleaseDirectory (Join-Path $script:WarehouseEpiReleasesRoot $Version)
$targetExecutable = Join-Path $targetDirectory 'WarehouseEPI.Web.exe'
if (-not (Test-Path -LiteralPath $targetExecutable -PathType Leaf)) { throw 'La versión solicitada no contiene el ejecutable esperado.' }
if ([IO.Path]::GetFullPath($targetExecutable) -eq [IO.Path]::GetFullPath($previousExecutable)) { throw 'La versión solicitada ya está activa.' }
Invoke-WarehouseEpiPreflight $targetExecutable

Stop-WarehouseEpiServiceSafely
try {
    Set-WarehouseEpiServiceBinary $targetExecutable
    Start-WarehouseEpiServiceAndVerify
}
catch {
    Stop-WarehouseEpiServiceSafely
    Set-WarehouseEpiServiceBinary $previousExecutable
    Start-WarehouseEpiServiceAndVerify
    throw 'El rollback solicitado falló y se restauró la versión que estaba activa.'
}
Remove-ExpiredWarehouseEpiReleases 2
Write-Host "WarehouseEPI volvió a la versión $Version."
