[CmdletBinding()]
param(
    [string]$ProjectPath = 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj',
    [string]$ConfigurationPath = 'C:\ProgramData\WarehouseEPI\Config\service-settings.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
$resolvedConfiguration = Resolve-WarehouseEpiChildPath $ConfigurationPath 'C:\ProgramData\WarehouseEPI\Config'
if (Test-Path -LiteralPath $resolvedConfiguration) { throw 'La configuración protegida del servicio ya existe.' }

$raw = @(& dotnet user-secrets list --json --project $ProjectPath 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'No fue posible leer User Secrets.' }
$begin = [Array]::IndexOf([object[]]$raw, '//BEGIN')
$end = [Array]::IndexOf([object[]]$raw, '//END')
if ($begin -lt 0 -or $end -le $begin) { throw 'User Secrets no devolvió JSON válido.' }
$secrets = (($raw[($begin + 1)..($end - 1)] -join [Environment]::NewLine) | ConvertFrom-Json)
$requiredKeys = @('AllowedHosts', 'ConnectionStrings:Warehouse', 'Security:PinLookupKey', 'Security:DataProtectionKeysPath', 'Security:ServerCertificateThumbprint')
foreach ($key in $requiredKeys) {
    if ([string]::IsNullOrWhiteSpace($secrets.$key)) { throw "Falta el secreto requerido '$key'." }
}

$settings = [ordered]@{
    AllowedHosts = $secrets.'AllowedHosts'
    ConnectionStrings = [ordered]@{ Warehouse = $secrets.'ConnectionStrings:Warehouse' }
    Security = [ordered]@{
        PinLookupKey = $secrets.'Security:PinLookupKey'
        DataProtectionKeysPath = $secrets.'Security:DataProtectionKeysPath'
        ServerCertificateThumbprint = $secrets.'Security:ServerCertificateThumbprint'
    }
    Observability = [ordered]@{
        LogDirectory = 'C:\ProgramData\WarehouseEPI\Logs'
        RetentionDays = 30
        FileSizeLimitMegabytes = 50
    }
}

$directory = Split-Path -Parent $resolvedConfiguration
New-Item -ItemType Directory -Force -Path $directory | Out-Null
& icacls $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger el directorio de configuración.' }
$temporaryPath = Join-Path $directory ".service-settings-$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
    & icacls $temporaryPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger la configuración del servicio.' }
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedConfiguration
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}
Write-Host 'La configuración protegida del servicio fue creada sin mostrar secretos.'
