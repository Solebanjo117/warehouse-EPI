[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:25[0-5]|2[0-4][0-9]|1?[0-9][0-9]?)(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9][0-9]?)){3}$')]
    [string]$ServerIpAddress,

    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,62}$')]
    [string]$ServerDnsName = 'warehouse-epi',

    [Parameter(Mandatory)]
    [string]$OfflineBackupDirectory,

    [string]$PublicCertificatePath = (Join-Path $PSScriptRoot '..\..\artifacts\security\warehouse-epi-local-ca.cer')
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

$resolvedBackupDirectory = [IO.Path]::GetFullPath($OfflineBackupDirectory)
$resolvedPublicCertificatePath = [IO.Path]::GetFullPath($PublicCertificatePath)
New-Item -ItemType Directory -Force -Path $resolvedBackupDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedPublicCertificatePath) | Out-Null

$backupPath = Join-Path $resolvedBackupDirectory 'warehouse-epi-local-ca.pfx'
if (Test-Path -LiteralPath $backupPath) {
    throw "Ya existe '$backupPath'. Elija un directorio de respaldo vacío para no sobrescribir la CA."
}

$backupPassword = Read-Host -AsSecureString 'Contraseña nueva para el respaldo PFX de la CA'
$ca = $null
try {
    $ca = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=Warehouse EPI Local CA' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 4096 `
        -KeyExportPolicy Exportable `
        -HashAlgorithm SHA256 `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}CA=true&pathlength=0') `
        -NotAfter (Get-Date).AddYears(10)

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

    Export-PfxCertificate -Cert $ca -FilePath $backupPath -Password $backupPassword | Out-Null
    Export-Certificate -Cert $ca -FilePath $resolvedPublicCertificatePath -Force | Out-Null

    Write-Host "Certificado del servidor instalado: $($server.Thumbprint)"
    Write-Host "Certificado público para tablets: $resolvedPublicCertificatePath"
    Write-Host "Respaldo cifrado de la CA: $backupPath"
    Write-Host 'Mueva el respaldo PFX fuera del servidor y conserve su contraseña por separado.'
}
finally {
    if ($null -ne $ca) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($ca.Thumbprint)" -Force
    }
}
