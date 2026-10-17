# Starts the Server Manager on every page named in a list with invented data (--fake), checks that it stays running, and saves a screenshot of each.
#   pwsh tools/ci/show-manager-pages.ps1 -Exe out/manager-win-x64/Remotely.ServerManager.exe -List tools/ci/manager-pages.txt -Out manager-pages
# Each line of the list is a Page class name (short name). Lines starting with # are ignored.
# Own copy of tools/ci/show-pages.ps1 for the Manager: the only differences are the extra '--fake' argument and that a page that exits early is reported with its crash log.
param([string]$Exe, [string]$List, [string]$Out, [int]$Seconds = 6)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$failed = 0
foreach ($line in Get-Content $List) {
  $name = $line.Trim()
  if ($name -eq '' -or $name.StartsWith('#')) { continue }
  $p = Start-Process $Exe -ArgumentList '--fake', '--show', $name -PassThru
  Start-Sleep -Seconds $Seconds
  if ($p.HasExited) {
    Write-Host "PAGE FAIL: $name exited with code $($p.ExitCode)"
    $crash = Join-Path $env:LOCALAPPDATA 'Remotely Server Manager/logs/manager-crash.log'
    if (Test-Path $crash) { Write-Host "--- manager-crash.log"; Get-Content $crash -Tail 30 }
    $failed++; continue
  }
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
  $bmp.Save("$Out/$name.png"); $bmp.Dispose()
  Stop-Process $p -Force -ErrorAction SilentlyContinue
  Write-Host "PAGE OK: $name"
}
if ($failed -gt 0) { throw "$failed page(s) failed to start" }
