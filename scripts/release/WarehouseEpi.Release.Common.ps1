Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:WarehouseEpiServiceName = 'WarehouseEPI'
$script:WarehouseEpiServiceIdentity = 'NT SERVICE\WarehouseEPI'
$script:WarehouseEpiRoot = 'C:\ProgramData\WarehouseEPI'
$script:WarehouseEpiReleasesRoot = 'C:\ProgramData\WarehouseEPI\Releases'
$script:WarehouseEpiConfigPath = 'C:\ProgramData\WarehouseEPI\Config\service-settings.json'

function Assert-WarehouseEpiAdministrator {
    if ($env:OS -ne 'Windows_NT') { throw 'Este script requiere Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecute PowerShell como administrador.'
    }
}

function Assert-WarehouseEpiVersion([string]$Version) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$') {
        throw 'La versión debe usar SemVer, por ejemplo 0.10.7.'
    }
}

function Resolve-WarehouseEpiChildPath([string]$Path, [string]$Root) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Se requiere una ruta absoluta.' }
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta debe permanecer dentro de '$Root'."
    }
    return $resolvedPath
}

function Get-WarehouseEpiService {
    return Get-CimInstance Win32_Service -Filter "Name='$script:WarehouseEpiServiceName'" -ErrorAction SilentlyContinue
}

function Get-WarehouseEpiExecutableFromService([object]$Service) {
    if ($null -eq $Service) { throw 'El servicio WarehouseEPI no está instalado.' }
    $match = [regex]::Match($Service.PathName, '^(?:"([^"]+)"|(\S+))')
    if (-not $match.Success) { throw 'No fue posible interpretar el ejecutable configurado para el servicio.' }
    $executable = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    return [IO.Path]::GetFullPath($executable)
}

function Test-WarehouseEpiReleaseDirectory([string]$Path) {
    $resolved = Resolve-WarehouseEpiChildPath $Path $script:WarehouseEpiReleasesRoot
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'La Release debe ser un directorio real, no un enlace o reparse point.'
    }
    if ([IO.Path]::GetFullPath($item.Parent.FullName) -ne [IO.Path]::GetFullPath($script:WarehouseEpiReleasesRoot)) {
        throw 'La Release debe ser hija directa del directorio de Releases.'
    }
    return $resolved
}

function Assert-WarehouseEpiTreeHasNoReparsePoints([string]$RootPath) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($RootPath)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Se encontró un reparse point no permitido en '$RootPath'."
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

function Expand-WarehouseEpiReleasePackage([string]$PackagePath) {
    $resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) { throw 'El paquete de Release no existe.' }
    New-Item -ItemType Directory -Force -Path $script:WarehouseEpiReleasesRoot | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stagingPath = Join-Path $script:WarehouseEpiReleasesRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $stagingPath | Out-Null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
        try {
            foreach ($entry in $archive.Entries) {
                if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Split(@('/', '\')).Contains('..')) {
                    throw 'El paquete contiene una ruta no segura.'
                }
                $destination = [IO.Path]::GetFullPath((Join-Path $stagingPath $entry.FullName))
                if (-not $destination.StartsWith(([IO.Path]::GetFullPath($stagingPath).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'El paquete intenta escribir fuera de su directorio temporal.'
                }
            }
        }
        finally { $archive.Dispose() }

        Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $stagingPath
        Assert-WarehouseEpiTreeHasNoReparsePoints $stagingPath
        $manifestPath = Join-Path $stagingPath 'release-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'El paquete no contiene release-manifest.json.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Assert-WarehouseEpiVersion $manifest.version
        if ($manifest.runtime -ne 'win-x64') { throw 'El paquete no corresponde a win-x64.' }
        if (-not (Test-Path -LiteralPath (Join-Path $stagingPath 'WarehouseEPI.Web.exe') -PathType Leaf)) { throw 'El paquete no contiene WarehouseEPI.Web.exe.' }

        foreach ($file in $manifest.files) {
            $filePath = [IO.Path]::GetFullPath((Join-Path $stagingPath $file.path))
            if (-not $filePath.StartsWith(([IO.Path]::GetFullPath($stagingPath).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw 'El manifiesto contiene una ruta inválida.' }
            if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $file.sha256) { throw 'La integridad del paquete no coincide con su manifiesto.' }
        }
        $declaredPaths = @($manifest.files | ForEach-Object { $_.path.Replace('\', '/') } | Sort-Object)
        $actualPaths = @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse |
            Where-Object { $_.FullName -ne $manifestPath } |
            ForEach-Object { [IO.Path]::GetRelativePath($stagingPath, $_.FullName).Replace('\', '/') } |
            Sort-Object)
        if (Compare-Object -ReferenceObject $declaredPaths -DifferenceObject $actualPaths) {
            throw 'El paquete contiene archivos no declarados o incompletos.'
        }

        $finalPath = Join-Path $script:WarehouseEpiReleasesRoot $manifest.version
        if (Test-Path -LiteralPath $finalPath) { throw "La Release '$($manifest.version)' ya está instalada." }
        Move-Item -LiteralPath $stagingPath -Destination $finalPath
        return [pscustomobject]@{ Version = $manifest.version; Path = $finalPath; Executable = (Join-Path $finalPath 'WarehouseEPI.Web.exe') }
    }
    catch {
        if (Test-Path -LiteralPath $stagingPath) {
            $staging = Get-Item -LiteralPath $stagingPath -Force
            if (($staging.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
                [IO.Path]::GetFullPath($staging.Parent.FullName) -eq [IO.Path]::GetFullPath($script:WarehouseEpiReleasesRoot)) {
                Remove-Item -LiteralPath $stagingPath -Recurse -Force
            }
        }
        throw
    }
}

function Test-WarehouseEpiPackageHash([string]$PackagePath) {
    $resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
    $hashPath = "$resolvedPackage.sha256"
    if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw 'Falta el archivo SHA-256 del paquete.' }
    $expected = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split '\s+')[0]
    if ($expected -notmatch '^[A-Fa-f0-9]{64}$') { throw 'El archivo SHA-256 no es válido.' }
    if ((Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash -ne $expected.ToUpperInvariant()) {
        throw 'El SHA-256 del paquete no coincide.'
    }
}

function Invoke-WarehouseEpiPreflight([string]$Executable, [string]$ConfigPath = $script:WarehouseEpiConfigPath) {
    $contentRoot = Split-Path -Parent $Executable
    & $Executable --environment Production --contentRoot=$contentRoot --ServiceConfigPath=$ConfigPath --validate-production 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La validación previa de producción falló.' }
}

function Grant-WarehouseEpiReleaseAccess([string]$ReleasePath) {
    $resolved = Test-WarehouseEpiReleaseDirectory $ReleasePath
    & icacls $resolved /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${script:WarehouseEpiServiceIdentity}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger la carpeta de Release.' }
}

function Grant-WarehouseEpiCertificateAccess([string]$Thumbprint) {
    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$normalized" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'El certificado configurado no tiene clave privada.' }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    try {
        if ($rsa -is [Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
        }
        else { throw 'El proveedor de la clave privada del certificado no es compatible.' }
    }
    finally { if ($null -ne $rsa) { $rsa.Dispose() } }
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { throw 'No fue posible localizar la clave privada del certificado.' }
    & icacls $keyPath /grant "${script:WarehouseEpiServiceIdentity}:R" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible conceder lectura de la clave HTTPS al servicio.' }
}

function Grant-WarehouseEpiServiceResources([string]$ReleasePath) {
    if (-not (Test-Path -LiteralPath $script:WarehouseEpiConfigPath -PathType Leaf)) { throw 'Falta la configuración protegida del servicio.' }
    $configuration = Get-Content -LiteralPath $script:WarehouseEpiConfigPath -Raw | ConvertFrom-Json
    $keysPath = Resolve-WarehouseEpiChildPath $configuration.Security.DataProtectionKeysPath $script:WarehouseEpiRoot
    $logsPath = Resolve-WarehouseEpiChildPath $configuration.Observability.LogDirectory $script:WarehouseEpiRoot
    $brandingDirectory = if ($null -ne $configuration.PSObject.Properties['Branding'] -and -not [string]::IsNullOrWhiteSpace($configuration.Branding.StorageDirectory)) { $configuration.Branding.StorageDirectory } else { 'C:\ProgramData\WarehouseEPI\Branding' }
    $brandingPath = Resolve-WarehouseEpiChildPath $brandingDirectory $script:WarehouseEpiRoot
    if (-not (Test-Path -LiteralPath $brandingPath -PathType Container)) { New-Item -ItemType Directory -Force -Path $brandingPath | Out-Null }
    foreach ($directory in @($keysPath, $logsPath, $brandingPath)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'Falta un directorio requerido por el servicio.' }
        & icacls $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${script:WarehouseEpiServiceIdentity}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger un directorio del servicio.' }
    }
    $configDirectory = Split-Path -Parent $script:WarehouseEpiConfigPath
    & icacls $script:WarehouseEpiReleasesRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${script:WarehouseEpiServiceIdentity}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger el directorio de Releases.' }
    & icacls $configDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${script:WarehouseEpiServiceIdentity}:(OI)(CI)RX" | Out-Null
    & icacls $script:WarehouseEpiConfigPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${script:WarehouseEpiServiceIdentity}:R" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger la configuración del servicio.' }
    Grant-WarehouseEpiReleaseAccess $ReleasePath
    Grant-WarehouseEpiCertificateAccess $configuration.Security.ServerCertificateThumbprint
}

function Assert-WarehouseEpiValidatedBackup(
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$PgRestorePath = 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe') {
    $resolved = Resolve-WarehouseEpiChildPath $BackupDirectory $script:WarehouseEpiRoot
    $latest = Get-ChildItem -LiteralPath $resolved -Filter 'warehouseEPI-*.dump' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $latest) { throw 'Se requiere al menos un respaldo 10.6 antes de instalar o actualizar.' }
    & $PgRestorePath --list $latest.FullName 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'El respaldo 10.6 más reciente no es válido.' }
}

function Set-WarehouseEpiServiceBinary([string]$Executable) {
    $contentRoot = Split-Path -Parent $Executable
    $commandLine = "`"$Executable`" --environment Production --contentRoot=`"$contentRoot`" --ServiceConfigPath=`"$script:WarehouseEpiConfigPath`""
    & sc.exe config $script:WarehouseEpiServiceName 'binPath=' $commandLine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible cambiar la versión activa del servicio.' }
}

function Wait-WarehouseEpiServiceState([string]$State, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $script:WarehouseEpiServiceName -ErrorAction Stop
        if ($service.Status.ToString() -eq $State) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "El servicio no alcanzó el estado $State."
}

function Start-WarehouseEpiServiceAndVerify {
    Start-Service -Name $script:WarehouseEpiServiceName
    Wait-WarehouseEpiServiceState 'Running'
    $configuration = Get-Content -LiteralPath $script:WarehouseEpiConfigPath -Raw | ConvertFrom-Json
    $healthHost = @(([string]$configuration.AllowedHosts).Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() })[0]
    if ([string]::IsNullOrWhiteSpace($healthHost) -or $healthHost -notmatch '^[A-Za-z0-9.-]+$') {
        throw 'AllowedHosts no contiene un host válido para comprobar el servicio local.'
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        & curl.exe --silent --fail --insecure --max-time 5 --header "Host: $healthHost" `
            'https://127.0.0.1/health/live' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'El servicio inició, pero el health check local no respondió correctamente.'
}

function Stop-WarehouseEpiServiceSafely {
    $service = Get-Service -Name $script:WarehouseEpiServiceName -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $script:WarehouseEpiServiceName -ErrorAction Stop
        Wait-WarehouseEpiServiceState 'Stopped'
    }
}

function Remove-ExpiredWarehouseEpiReleases([int]$PreviousVersionsToKeep = 2) {
    $service = Get-WarehouseEpiService
    $activeDirectory = Split-Path -Parent (Get-WarehouseEpiExecutableFromService $service)
    $releases = Get-ChildItem -LiteralPath $script:WarehouseEpiReleasesRoot -Directory -Force |
        Where-Object { $_.Name -match '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$' } |
        Sort-Object LastWriteTimeUtc -Descending
    $keptPrevious = 0
    foreach ($release in $releases) {
        $resolved = Test-WarehouseEpiReleaseDirectory $release.FullName
        if ([IO.Path]::GetFullPath($resolved) -eq [IO.Path]::GetFullPath($activeDirectory)) { continue }
        if ($keptPrevious -lt $PreviousVersionsToKeep) { $keptPrevious++; continue }
        Assert-WarehouseEpiTreeHasNoReparsePoints $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Remove-WarehouseEpiInactiveRelease([string]$ReleasePath) {
    $resolved = Test-WarehouseEpiReleaseDirectory $ReleasePath
    $service = Get-WarehouseEpiService
    if ($null -ne $service) {
        $activeDirectory = Split-Path -Parent (Get-WarehouseEpiExecutableFromService $service)
        if ([IO.Path]::GetFullPath($resolved) -eq [IO.Path]::GetFullPath($activeDirectory)) {
            throw 'No se puede eliminar la Release activa.'
        }
    }
    Assert-WarehouseEpiTreeHasNoReparsePoints $resolved
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
