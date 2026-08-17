[CmdletBinding()]
param(
    [string]$CredentialsPath = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$DatabaseUser = 'postgres',
    [string]$ServiceIdentity = 'SYSTEM',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') { throw 'Este script requiere Windows.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute PowerShell como administrador para crear las credenciales de respaldo.'
}

$resolvedPath = [IO.Path]::GetFullPath($CredentialsPath)
if (-not $resolvedPath.StartsWith('C:\ProgramData\WarehouseEPI\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La ruta de credenciales debe permanecer dentro de C:\ProgramData\WarehouseEPI.'
}
if ((Test-Path -LiteralPath $resolvedPath) -and -not $Force) {
    throw "Ya existe '$resolvedPath'. Use -Force solo para reemplazar credenciales de respaldo deliberadamente."
}

$password = Read-Host -AsSecureString 'Contraseña del usuario PostgreSQL para respaldo'
$plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
try {
    $escapedPassword = $plainPassword.Replace('\', '\\').Replace(':', '\:')
    $directory = Split-Path -Parent $resolvedPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [IO.File]::WriteAllText($resolvedPath, "${DatabaseHost}:${DatabasePort}:*:${DatabaseUser}:${escapedPassword}", [Text.UTF8Encoding]::new($false))
}
finally {
    if ($null -ne $plainPassword) { $plainPassword = $null }
}

& icacls $resolvedPath /inheritance:r | Out-Null
& icacls $resolvedPath /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
if ($ServiceIdentity -ne 'SYSTEM') { & icacls $resolvedPath /grant:r "${ServiceIdentity}:R" | Out-Null }
if ($LASTEXITCODE -ne 0) { throw "No fue posible configurar ACL para '$resolvedPath'." }

Write-Host "Las credenciales de respaldo fueron protegidas en '$resolvedPath'."
