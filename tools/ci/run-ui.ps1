# Runs the whole UI check on this Windows machine: every variant in tools/ci/ui-variants.txt (theme, scaling, high contrast) over its pages, the Server Manager pages,
# the UI Automation flows, and the comparison of the screenshots with the baselines in docs/screenshots/ui/<rid>.
#   pwsh tools/ci/run-ui.ps1 -Rid win-x64 -App out/app-win-x64/Remotely.exe -Manager out/manager-win-x64/Remotely.ServerManager.exe -Out ui-out [-UpdateBaseline]
# Results: <Out>/<rid>/... screenshots and UIA tree dumps, <Out>/<rid>/summary.md. Exit code 1 when any page, flow or comparison failed.
param(
  [Parameter(Mandatory)][string]$Rid,
  [Parameter(Mandatory)][string]$App,
  [string]$Manager = '',
  [string]$Out = 'ui-out',
  [switch]$UpdateBaseline,
  [switch]$StrictContent
)
$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$uitests = & (Join-Path $PSScriptRoot 'build-uitests.ps1') | Select-Object -Last 1
$strict = if ($StrictContent) { 'true' } else { 'false' }
$raw = Join-Path $Out $Rid
$masked = Join-Path "$Out-masked" $Rid
$baseline = Join-Path $root "docs/screenshots/ui/$Rid"
New-Item -ItemType Directory -Force $raw, $masked | Out-Null
$summary = New-Object System.Collections.Generic.List[string]
$failures = 0
function Note($text) { $t = ($text -join "`n"); Write-Host $t; $summary.Add($t) }

Note "## UI check on $Rid"
& $uitests system --info | Tee-Object -Variable info | Out-Host
Note ($info -join "`n")
# Runners often start at 1024x768, too small for a 200% window; ask for the largest mode up to full HD.
& $uitests system --res 1920x1080 | Tee-Object -Variable resInfo | Out-Host
Note ($resInfo -join "`n")

# What else has a window on this machine (an out-of-box-experience screen or a dialog would cover the app and ruin every screenshot).
$windows = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { "$($_.ProcessName) pid $($_.Id): '$($_.MainWindowTitle)'" }
Note ("Processes with a main window before the run:`n" + ($windows -join "`n"))

$variants = Get-Content (Join-Path $PSScriptRoot 'ui-variants.txt') | Where-Object { $_.Trim() -ne '' -and -not $_.StartsWith('#') }
foreach ($line in $variants) {
  $c = $line -split '\s+'
  $name = $c[0]; $theme = $c[1]; $scale = $c[2]; $hc = $c[3]; $list = Join-Path $root $c[4]; $withManager = $c[5] -eq 'yes'
  Note "### variant $name (theme $theme, scaling $scale%, high contrast $hc)"
  $refused = @()
  & $uitests system --scale $scale | Tee-Object -Variable r1 | Out-Host
  if ($LASTEXITCODE -ne 0) { $refused += ($r1 | Where-Object { $_ -match 'REFUSED' }) }
  & $uitests system --hc $hc | Tee-Object -Variable r2 | Out-Host
  if ($LASTEXITCODE -ne 0) { $refused += ($r2 | Where-Object { $_ -match 'REFUSED' }) }
  Note (($r1 + $r2) -join "`n")
  if ($refused.Count -gt 0) { Note "SKIPPED variant ${name}: $($refused -join ' ')"; continue }

  & $uitests pages --exe $App --list $list --out (Join-Path $raw $name) --masked (Join-Path $masked $name) --theme $theme --strict-content $strict | Tee-Object -Variable pageLog | Out-Host
  if ($LASTEXITCODE -ne 0) { $failures++ }
  Note ($pageLog | Where-Object { $_ -match '^PAGE|passed$' })
  if ($withManager -and $Manager -ne '') {
    & $uitests pages --manager --exe $Manager --list (Join-Path $PSScriptRoot 'manager-pages.txt') --out (Join-Path $raw "$name/manager") --masked (Join-Path $masked "$name/manager") --theme $theme --strict-content $strict | Tee-Object -Variable mLog | Out-Host
    if ($LASTEXITCODE -ne 0) { $failures++ }
    Note ($mLog | Where-Object { $_ -match '^PAGE|passed$' })
  }
}
# Back to the normal display state for the flows.
& $uitests system --scale 100 | Out-Host
& $uitests system --hc off | Out-Host

Note "### UI Automation flows"
& $uitests flows --exe $App --out (Join-Path $raw 'flows') --masked (Join-Path $masked 'flows') --theme dark --repo $root | Tee-Object -Variable flowLog | Out-Host
if ($LASTEXITCODE -ne 0) { $failures++ }
Note ($flowLog | Where-Object { $_ -match '^FLOW|^\s+(ok|FAILED):' })

if ($UpdateBaseline) {
  & $uitests baseline --actual $masked --baseline $baseline | Out-Host
  Note "Baselines in docs/screenshots/ui/$Rid were replaced with this run (masked copies) because the run was started with update_baseline."
} else {
  Note "### Comparison with docs/screenshots/ui/$Rid"
  $diffReport = Join-Path $raw 'diff.md'
  & $uitests diff --actual $masked --baseline $baseline --diffs (Join-Path $raw 'diffs') --report $diffReport | Tee-Object -Variable diffLog | Out-Host
  if ($LASTEXITCODE -ne 0) { $failures++ }
  Note ($diffLog | Select-Object -Last 1)
  Note ($diffLog | Where-Object { $_ -match 'DRIFT|MISSING' })
}

$summary | Set-Content (Join-Path $raw 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { Get-Content (Join-Path $raw 'summary.md') | Add-Content $env:GITHUB_STEP_SUMMARY }
if ($failures -gt 0) { Write-Host "UI CHECK FAILED: $failures part(s) failed"; exit 1 }
Write-Host "UI CHECK PASSED"
