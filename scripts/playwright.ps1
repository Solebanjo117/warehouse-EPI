# Forward arguments directly to Node; do not pass URLs or code through cmd.exe.
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CliArguments
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cliPath = Join-Path $repositoryRoot 'tools/playwright/node_modules/@playwright/cli/playwright-cli.js'

if (-not (Test-Path -LiteralPath $cliPath)) {
    throw 'Playwright CLI no está instalado. Ejecuta pnpm --dir tools/playwright install --frozen-lockfile --ignore-scripts desde la raíz.'
}

$nodeCommand = Get-Command node -CommandType Application -ErrorAction Stop
if (-not $CliArguments) {
    $CliArguments = @('--help')
}

Push-Location -LiteralPath $repositoryRoot
$previousUpdateNotifier = [Environment]::GetEnvironmentVariable('NO_UPDATE_NOTIFIER', 'Process')
try {
    # The version and adapted skill are pinned; updates are deliberate and offline use is supported.
    $env:NO_UPDATE_NOTIFIER = '1'
    & $nodeCommand.Source $cliPath @CliArguments
    $cliExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('NO_UPDATE_NOTIFIER', $previousUpdateNotifier, 'Process')
    Pop-Location
}

exit $cliExitCode
