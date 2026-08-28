[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [switch]$RequireExternalHash
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) {
    throw 'El paquete de migración indicado no existe.'
}

$externalHashPath = "$resolvedPackage.sha256"
if ($RequireExternalHash -and -not (Test-Path -LiteralPath $externalHashPath -PathType Leaf)) {
    throw 'Falta el archivo SHA-256 externo pareado con el paquete.'
}
if (Test-Path -LiteralPath $externalHashPath -PathType Leaf) {
    $hashLine = (Get-Content -LiteralPath $externalHashPath -Raw).Trim()
    if ($hashLine -notmatch '^([a-fA-F0-9]{64})  (.+)$' -or
        $Matches[2] -cne (Split-Path -Leaf $resolvedPackage)) {
        throw 'El archivo SHA-256 externo tiene un formato o nombre de paquete inválido.'
    }
    $actualPackageHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
    if ($actualPackageHash -cne $Matches[1].ToUpperInvariant()) {
        throw 'El SHA-256 externo no coincide con el paquete de migración.'
    }
}

Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    foreach ($archiveEntry in $archive.Entries) {
        $entryPath = $archiveEntry.FullName
        if ([string]::IsNullOrWhiteSpace($entryPath) -or
            $entryPath.Contains('\') -or
            $entryPath.StartsWith('/') -or
            $entryPath.Split('/') -contains '..') {
            throw "El ZIP contiene una ruta insegura: '$entryPath'."
        }
    }
    $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    $manifestEntry = @($entries | Where-Object { $_.FullName -ceq 'manifest.json' })
    if ($manifestEntry.Count -ne 1) { throw 'El paquete debe contener un solo manifest.json en la raíz.' }

    $manifestReader = [IO.StreamReader]::new($manifestEntry[0].Open(), [Text.Encoding]::UTF8)
    try { $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json }
    finally { $manifestReader.Dispose() }

    if ($manifest.SchemaVersion -ne 1 -or $manifest.PackageType -cne 'WarehouseEPI-MigrationBackup') {
        throw 'El manifiesto del paquete no corresponde a una migración compatible de Warehouse EPI.'
    }
    if ($manifest.ContainsSecrets -ne $false) {
        throw 'El manifiesto no confirma la exclusión de secretos.'
    }

    $requiredSecrets = @($manifest.RequiredExternalSecrets)
    foreach ($requiredSecret in @('Security:PinLookupKey', 'PostgreSQL credentials', 'LAN CA PFX')) {
        if ($requiredSecrets -cnotcontains $requiredSecret) {
            throw "El manifiesto no declara el secreto externo requerido '$requiredSecret'."
        }
    }

    $manifestFiles = @($manifest.Files)
    if ($manifestFiles.Count -lt 3) { throw 'El manifiesto no contiene todos los componentes mínimos.' }
    $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $manifestFiles) {
        $relativePath = [string]$file.Path
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            $relativePath.Contains('\') -or
            $relativePath.StartsWith('/') -or
            $relativePath.Split('/') -contains '..' -or
            -not $manifestPaths.Add($relativePath)) {
            throw "El manifiesto contiene una ruta insegura o duplicada: '$relativePath'."
        }
        if ([long]$file.Length -lt 0 -or [string]$file.Sha256 -notmatch '^[a-f0-9]{64}$') {
            throw "El manifiesto contiene metadatos inválidos para '$relativePath'."
        }
    }

    $archivePaths = @($entries | Where-Object { $_.FullName -cne 'manifest.json' } | ForEach-Object { $_.FullName })
    if ($archivePaths.Count -ne $manifestFiles.Count) {
        throw 'El número de archivos del ZIP no coincide con el manifiesto.'
    }
    foreach ($archivePath in $archivePaths) {
        if (-not $manifestPaths.Contains($archivePath)) {
            throw "El ZIP contiene un archivo no declarado: '$archivePath'."
        }
    }

    $databaseFiles = @($manifestFiles | Where-Object { $_.Kind -ceq 'database' })
    $referenceFiles = @($manifestFiles | Where-Object { $_.Kind -ceq 'references' })
    $instructions = @($manifestFiles | Where-Object { $_.Kind -ceq 'instructions' })
    if ($databaseFiles.Count -ne 1 -or $databaseFiles[0].Path -notmatch '^database/warehouseEPI-[0-9]{8}-[0-9]{6}\.dump$') {
        throw 'El paquete no contiene exactamente un respaldo PostgreSQL reconocido.'
    }
    if ($referenceFiles.Count -ne 1 -or $referenceFiles[0].Path -notmatch '^references/warehouseEPI-[0-9]{8}-[0-9]{6}-references\.zip$') {
        throw 'El paquete no contiene exactamente el ZIP pareado de referencias.'
    }
    if ($instructions.Count -ne 1 -or $instructions[0].Path -cne 'RESTORE.txt') {
        throw 'El paquete no contiene las instrucciones de recuperación.'
    }

    foreach ($file in $manifestFiles) {
        $entry = @($entries | Where-Object { $_.FullName -ceq [string]$file.Path })
        if ($entry.Count -ne 1 -or $entry[0].Length -ne [long]$file.Length) {
            throw "El tamaño de '$($file.Path)' no coincide con el manifiesto."
        }
        $stream = $entry[0].Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
        finally { $stream.Dispose() }
        if ($hash -cne [string]$file.Sha256) {
            throw "El hash SHA-256 de '$($file.Path)' no coincide con el manifiesto."
        }
    }

    Write-Host "Paquete de migración íntegro: $(Split-Path -Leaf $resolvedPackage)"
    $manifest
}
finally {
    $archive.Dispose()
}
