# Starts the Server Manager on every page named in a list with invented data (--fake) and checks each one like tools/ci/show-pages.ps1 does
# (alive, no manager-crash.log, a visible window, real content in the UI Automation tree, no extra window, a non-blank screenshot).
#   pwsh tools/ci/show-manager-pages.ps1 -Exe out/manager-win-x64/Remotely.ServerManager.exe -List tools/ci/manager-pages.txt -Out manager-pages [-Theme dark]
# Each line of the list is a Page class name (short name). Lines starting with # are ignored.
param([string]$Exe, [string]$List, [string]$Out, [int]$Seconds = 2, [string]$Theme = 'dark', [switch]$StrictContent)
$ErrorActionPreference = 'Stop'
$uitests = & (Join-Path $PSScriptRoot 'build-uitests.ps1') | Select-Object -Last 1
$strict = if ($StrictContent) { 'true' } else { 'false' }
& $uitests pages --manager --exe $Exe --list $List --out $Out --theme $Theme --settle $Seconds --strict-content $strict
if ($LASTEXITCODE -ne 0) {
  $crash = Join-Path $env:LOCALAPPDATA 'Remotely Server Manager/logs/manager-crash.log'
  if (Test-Path $crash) { Write-Host "--- manager-crash.log"; Get-Content $crash -Tail 30 }
  throw "one or more Server Manager pages failed (see the PAGE FAIL lines above)"
}
