param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After
)
$ErrorActionPreference = 'Stop'
$beforeSamples = @(Get-Content -LiteralPath $Before -Raw | ConvertFrom-Json)
$afterSamples = @(Get-Content -LiteralPath $After -Raw | ConvertFrom-Json)
function Median($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Missing performance samples.' }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2
}
foreach ($explicitWeek in @($false, $true)) {
    foreach ($count in @(1, 10, 25, 100)) {
        $previous = @($beforeSamples | Where-Object { $_.explicitWeek -eq $explicitWeek -and $_.cells -eq $count })
        $current = @($afterSamples | Where-Object { $_.explicitWeek -eq $explicitWeek -and $_.cells -eq $count })
        if ($previous.Count -ne 5 -or $current.Count -ne 5) { throw "Expected five measured iterations for $explicitWeek / $count." }
        foreach ($phase in @('review', 'confirmation')) {
            $oldTime = Median ($previous | ForEach-Object { $_.$phase.milliseconds })
            $newTime = Median ($current | ForEach-Object { $_.$phase.milliseconds })
            [pscustomobject]@{
                ExplicitCarryover = $explicitWeek; Cells = $count; Phase = $phase
                BeforeMs = [Math]::Round($oldTime, 2); AfterMs = [Math]::Round($newTime, 2)
                ReductionPercent = if ($oldTime -gt 0) { [Math]::Round(100 * ($oldTime - $newTime) / $oldTime, 1) } else { $null }
                BeforeQueries = Median ($previous | ForEach-Object { $_.$phase.Commands })
                AfterQueries = Median ($current | ForEach-Object { $_.$phase.Commands })
                BeforeSaves = Median ($previous | ForEach-Object { $_.$phase.Count })
                AfterSaves = Median ($current | ForEach-Object { $_.$phase.Count })
            }
        }
    }
}
