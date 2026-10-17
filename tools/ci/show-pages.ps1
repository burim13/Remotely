# Starts the app on every page named in a list and checks each one properly, then saves a screenshot of that page's window.
#   pwsh tools/ci/show-pages.ps1 -Exe out/app-win-x64/Remotely.exe -List tools/ci/pages.txt -Out pages [-Theme dark] [-Seconds 2] [-StrictContent]
# Each line of the list is a Page class name (short name). Lines starting with # are ignored. Agents add their own page names to tools/ci/pages.txt.
#
# A page passes only when ALL of this holds (the work is done by tests/Remotely.UITests, built on first use):
#   - the process is still running and no app-crash.log was written,
#   - a visible top-level window of the process appeared and its size stayed the same for 600 ms,
#   - the window's UI Automation tree holds real content (at least three named elements besides the caption buttons),
#   - with -StrictContent: an element whose class name or automation id is the page's class name is in that tree,
#   - there is no unexpected second window,
#   - the screenshot of the window is not blank.
# The UI Automation tree of every page is saved next to the screenshot in <Out>/tree/<Page>.txt.
param([string]$Exe, [string]$List, [string]$Out, [int]$Seconds = 2, [string]$Theme = 'dark', [switch]$StrictContent)
$ErrorActionPreference = 'Stop'
$uitests = & (Join-Path $PSScriptRoot 'build-uitests.ps1') | Select-Object -Last 1
$strict = if ($StrictContent) { 'true' } else { 'false' }
& $uitests pages --exe $Exe --list $List --out $Out --theme $Theme --settle $Seconds --strict-content $strict
if ($LASTEXITCODE -ne 0) { throw "one or more pages failed (see the PAGE FAIL lines above)" }
