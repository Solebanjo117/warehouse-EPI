[CmdletBinding()]
param(
    [string]$KeysPath = 'C:\ProgramData\WarehouseEPI\DataProtection-Keys',
    [string]$ServiceIdentity
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Este script requiere Windows.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute PowerShell como administrador para aplicar ACL a las claves de Data Protection.'
}

$resolvedPath = [IO.Path]::GetFullPath($KeysPath)
if (-not $resolvedPath.StartsWith('C:\ProgramData\WarehouseEPI\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La ruta de claves debe permanecer dentro de C:\ProgramData\WarehouseEPI.'
}

New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $resolvedPath /inheritance:r | Out-Null
& icacls $resolvedPath /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${currentUser}:(OI)(CI)M" | Out-Null
if (-not [string]::IsNullOrWhiteSpace($ServiceIdentity)) {
    & icacls $resolvedPath /grant:r "${ServiceIdentity}:(OI)(CI)M" | Out-Null
}
if ($LASTEXITCODE -ne 0) {
    throw "No fue posible configurar ACL para '$resolvedPath'."
}

Write-Host "Las claves de Data Protection están listas en '$resolvedPath'."
