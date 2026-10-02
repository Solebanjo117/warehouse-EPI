Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WarehouseEpiCertificateDnsNames([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $extension = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' })
    if ($extension.Count -eq 0) { return @() }
    $san = [Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension]::new()
    $san.CopyFrom($extension[0])
    return @($san.EnumerateDnsNames())
}

function Resolve-WarehouseEpiBundleFile([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains(':') -or
        [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Split(@('/', '\')) -contains '..') {
        throw 'El paquete contiene una ruta insegura.'
    }
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not $resolved.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'El archivo debe permanecer dentro del paquete.'
    }
    $current = $resolved
    while ($current.Length -ge $resolvedRoot.TrimEnd('\', '/').Length) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'El paquete no admite enlaces ni reparse points.'
        }
        $current = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($current)) { break }
    }
    return $resolved
}

function Get-WarehouseEpiSqlMigrationIds([string]$Sql) {
    return @([regex]::Matches($Sql,
        'INSERT\s+INTO\s+"__EFMigrationsHistory"\s*\([^;]+?\)\s*VALUES\s*\(\s*''(?<id>\d{14}_[A-Za-z0-9_]+)''',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase) |
        ForEach-Object { $_.Groups['id'].Value } | Sort-Object -Unique)
}

function Assert-WarehouseEpiMigrationSet([string[]]$Expected, [string[]]$Actual) {
    if ($Expected.Count -eq 0 -or $Actual.Count -eq 0 -or
        (Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual -CaseSensitive)) {
        throw 'Las migraciones de la base, el SQL y la Release deben coincidir.'
    }
}

function Read-WarehouseEpiServerRelease([string]$PackagePath) {
    $resolved = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath "$resolved.sha256" -PathType Leaf)) { throw 'Falta el SHA-256 de la Release.' }
    $expectedHash = ((Get-Content -LiteralPath "$resolved.sha256" -Raw).Trim() -split '\s+')[0]
    if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'El SHA-256 de la Release no coincide.'
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name })
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $null = Resolve-WarehouseEpiBundleFile ([IO.Path]::GetDirectoryName($resolved)) $entry.FullName
            if (-not $paths.Add($entry.FullName.Replace('\', '/')) -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw 'La Release contiene archivos duplicados o enlaces.'
            }
        }
        $manifestEntry = @($entries | Where-Object { $_.FullName -ceq 'release-manifest.json' })
        if ($manifestEntry.Count -ne 1) { throw 'Falta el manifiesto de la Release.' }
        $reader = [IO.StreamReader]::new($manifestEntry[0].Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($manifest.runtime -ne 'win-x64' -or $manifest.selfContained -ne $true -or
            $manifest.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$' -or
            $null -eq $manifest.PSObject.Properties['migrationIds'] -or @($manifest.migrationIds).Count -eq 0) {
            throw 'Publique una Release autocontenida win-x64 con el publicador actualizado; faltan sus migraciones.'
        }
        $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $manifest.files) {
            $null = Resolve-WarehouseEpiBundleFile ([IO.Path]::GetDirectoryName($resolved)) $file.path
            if (-not $declared.Add($file.path)) { throw 'El manifiesto de Release contiene rutas duplicadas.' }
            $entry = $archive.GetEntry($file.path)
            if ($null -eq $entry -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'La Release está incompleta.' }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -ne $file.sha256) { throw 'Un archivo de la Release no coincide con su hash.' }
        }
        if ($entries.Count -ne $declared.Count + 1 -or -not $declared.Contains('WarehouseEPI.Web.exe')) {
            throw 'La Release contiene archivos no declarados o no incluye el ejecutable.'
        }
        return $manifest
    }
    finally { $archive.Dispose() }
}

function Test-WarehouseEpiServerBundle([string]$BundleDirectory) {
    $manifestPath = Resolve-WarehouseEpiBundleFile $BundleDirectory 'server-bundle.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.packageType -cne 'WarehouseEPI-Server') {
        throw 'El directorio no contiene un paquete de instalación compatible.'
    }
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $path = Resolve-WarehouseEpiBundleFile $BundleDirectory $file.path
        if (-not $declared.Add($file.path) -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw 'El paquete está incompleto o fue modificado; vuelva a copiar el ZIP original.'
        }
    }
    foreach ($required in @($manifest.releasePackage, $manifest.schemaFile, 'Install.ps1',
        'scripts/server/Install-WarehouseEpiServer.ps1', 'scripts/server/WarehouseEpi.Server.Common.ps1',
        'scripts/release/Install-WarehouseEpiService.ps1', 'scripts/release/WarehouseEpi.Release.Common.ps1')) {
        if (-not $declared.Contains($required)) { throw 'El manifiesto no declara todos los componentes de instalación.' }
    }
    $release = Read-WarehouseEpiServerRelease (Resolve-WarehouseEpiBundleFile $BundleDirectory $manifest.releasePackage)
    $sqlIds = @(Get-WarehouseEpiSqlMigrationIds (Get-Content -LiteralPath (Resolve-WarehouseEpiBundleFile $BundleDirectory $manifest.schemaFile) -Raw))
    Assert-WarehouseEpiMigrationSet @($release.migrationIds) $sqlIds
    if ($release.version -cne $manifest.version) { throw 'La versión del paquete no coincide con la Release.' }
    return $manifest
}

function Read-WarehouseEpiHiddenValue([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secure.Dispose() }
}

function Write-WarehouseEpiPrivateFile([string]$Path, [string]$Content) {
    $root = 'C:\ProgramData\WarehouseEPI'
    $resolved = Resolve-WarehouseEpiBundleFile $root ([IO.Path]::GetRelativePath($root, $Path))
    $directory = Split-Path -Parent $resolved
    $null = New-Item -ItemType Directory -Force -Path $directory
    & icacls $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger el directorio de configuración.' }
    $temporary = Join-Path $directory ".setup-$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
        & icacls $temporary /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No fue posible proteger el archivo de configuración.' }
        Move-Item -LiteralPath $temporary -Destination $resolved -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function ConvertTo-WarehouseEpiPgPass([string]$Password) {
    if ([string]::IsNullOrWhiteSpace($Password) -or $Password.IndexOfAny([char[]]"`r`n") -ge 0) {
        throw 'La contraseña PostgreSQL no puede estar vacía ni contener saltos de línea.'
    }
    return 'localhost:5432:*:postgres:' + $Password.Replace('\', '\\').Replace(':', '\:')
}

function Invoke-WarehouseEpiSetupSql([string]$PsqlPath, [string]$Database, [string]$Sql,
    [string]$PassFile, [string]$User = 'postgres') {
    $previousPassFile = $env:PGPASSFILE
    $previousPassword = $env:PGPASSWORD
    $previousEncoding = $OutputEncoding
    $env:PGPASSFILE = $PassFile
    $env:PGPASSWORD = $null
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $result = @($Sql | & $PsqlPath --no-psqlrc --no-password --quiet --tuples-only --no-align `
            --host=localhost --port=5432 --username=$User --dbname=$Database --set=ON_ERROR_STOP=1 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Falló una comprobación PostgreSQL. Revise servicio, credenciales y compatibilidad del esquema.' }
        return $result
    }
    finally { $env:PGPASSFILE = $previousPassFile; $env:PGPASSWORD = $previousPassword; $OutputEncoding = $previousEncoding }
}

function New-WarehouseEpiScramVerifier([string]$AsciiPassword) {
    if ($AsciiPassword -notmatch '^[A-Za-z0-9+/=]+$') { throw 'El asistente requiere una contraseña generada en Base64.' }
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $salted = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [Text.Encoding]::UTF8.GetBytes($AsciiPassword), $salt, 4096, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $hmac = [Security.Cryptography.HMACSHA256]::new($salted)
    try {
        $client = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Client Key'))
        $stored = [Security.Cryptography.SHA256]::HashData($client)
        $server = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Server Key'))
        return 'SCRAM-SHA-256$4096:' + [Convert]::ToBase64String($salt) + '$' +
            [Convert]::ToBase64String($stored) + ':' + [Convert]::ToBase64String($server)
    }
    finally { $hmac.Dispose(); [Array]::Clear($salted); [Array]::Clear($client) }
}
