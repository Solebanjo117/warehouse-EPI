[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiVersion $Version

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dirty = & git -C $repositoryRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'No fue posible comprobar el estado de Git.' }
if ($dirty) { throw 'La publicación requiere un worktree limpio y confirmado.' }
$commit = (& git -C $repositoryRoot rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'No fue posible determinar el commit de la Release.' }

$outputRoot = Join-Path $repositoryRoot 'artifacts\releases'
$releaseName = "WarehouseEPI-$Version-win-x64"
$releaseDirectory = Join-Path $outputRoot $releaseName
$packagePath = "$releaseDirectory.zip"
$hashPath = "$packagePath.sha256"
$buildArtifacts = Join-Path $outputRoot ".build-$Version-win-x64"
if ((Test-Path -LiteralPath $releaseDirectory) -or (Test-Path -LiteralPath $packagePath) -or
    (Test-Path -LiteralPath $hashPath) -or (Test-Path -LiteralPath $buildArtifacts)) {
    throw "Ya existe un artefacto para la versión '$Version'."
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
& dotnet publish (Join-Path $repositoryRoot 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:Version=$Version -p:InformationalVersion=$Version -p:RestoreLockedMode=true `
    --artifacts-path $buildArtifacts --output $releaseDirectory
if ($LASTEXITCODE -ne 0) { throw 'La publicación Release falló.' }
if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory 'WarehouseEPI.Web.exe') -PathType Leaf)) {
    throw 'La publicación no generó WarehouseEPI.Web.exe.'
}

$files = Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($releaseDirectory, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    gitCommit = $commit
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseDirectory 'release-manifest.json') -Encoding utf8NoBOM
Compress-Archive -Path (Join-Path $releaseDirectory '*') -DestinationPath $packagePath
$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
Set-Content -LiteralPath $hashPath -Value "$packageHash  $(Split-Path -Leaf $packagePath)" -Encoding ascii
Write-Host "Release creada: $packagePath"
