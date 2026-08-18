[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:25[0-5]|2[0-4][0-9]|1?[0-9][0-9]?)(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9][0-9]?)){3}$')]
    [string]$ServerIpAddress,

    [Parameter(Mandatory)]
    [string]$CaPfxPath,

    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,62}$')]
    [string]$ServerDnsName = 'warehouse-epi',

    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\..\src\WarehouseEPI.Web\WarehouseEPI.Web.csproj')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Este script requiere Windows y el almacén de certificados de la máquina local.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute PowerShell como administrador para instalar el certificado del servidor.'
}

$resolvedCaPfxPath = [IO.Path]::GetFullPath($CaPfxPath)
$resolvedProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path -LiteralPath $resolvedCaPfxPath -PathType Leaf)) {
    throw "No existe el respaldo PFX '$resolvedCaPfxPath'."
}
if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Leaf)) {
    throw "No existe el proyecto web '$resolvedProjectPath'."
}

$existingCurrentUserThumbprints = @(
    Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | ForEach-Object Thumbprint
)
$importedCertificates = @()
$server = $null
try {
    $password = Read-Host -AsSecureString 'Contraseña del respaldo PFX de la CA'
    $importedCertificates = @(
        Import-PfxCertificate `
            -FilePath $resolvedCaPfxPath `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -Password $password
    )

    $ca = $importedCertificates | Where-Object {
        $certificate = $_
        $basicConstraints = $certificate.Extensions |
            Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
            Select-Object -First 1
        $certificate.HasPrivateKey -and $null -ne $basicConstraints -and $basicConstraints.CertificateAuthority
    } | Select-Object -First 1
    if ($null -eq $ca) {
        throw 'El PFX no contiene una CA con clave privada.'
    }

    $server = New-SelfSignedCertificate `
        -Type Custom `
        -Subject "CN=$ServerDnsName" `
        -Signer $ca `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -TextExtension @(
            "2.5.29.17={text}DNS=$ServerDnsName&IPAddress=$ServerIpAddress",
            '2.5.29.37={text}1.3.6.1.5.5.7.3.1'
        ) `
        -NotAfter (Get-Date).AddDays(365)

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        $dotnetPath = 'C:\Program Files\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
            throw 'No se encontró dotnet para actualizar User Secrets.'
        }
    }
    else {
        $dotnetPath = $dotnet.Source
    }

    & $dotnetPath user-secrets set 'AllowedHosts' "$ServerDnsName;$ServerIpAddress" --project $resolvedProjectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible actualizar AllowedHosts en User Secrets.' }
    & $dotnetPath user-secrets set 'Security:ServerCertificateThumbprint' $server.Thumbprint --project $resolvedProjectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible actualizar la huella del certificado en User Secrets.' }

    Write-Host "Certificado del servidor instalado para $ServerDnsName y $ServerIpAddress."
    Write-Host 'User Secrets fue actualizado sin mostrar secretos.'
    Write-Host 'Detenga la aplicación actual y vuelva a iniciarla para activar el certificado.'
}
finally {
    foreach ($certificate in $importedCertificates) {
        if ($certificate.Thumbprint -notin $existingCurrentUserThumbprints) {
            Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
        }
    }
}
