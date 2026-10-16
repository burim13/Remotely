# Starts the app on every page named in a list, checks that it stays running, and saves a screenshot of each.
#   pwsh tools/ci/show-pages.ps1 -Exe out/app-win-x64/Remotely.exe -List tools/ci/pages.txt -Out pages [-Theme dark]
# Each line of the list is a Page class name (short name). Lines starting with # are ignored. Agents add their own page names to tools/ci/pages.txt.
param([string]$Exe, [string]$List, [string]$Out, [int]$Seconds = 6)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$failed = 0
foreach ($line in Get-Content $List) {
  $name = $line.Trim()
  if ($name -eq '' -or $name.StartsWith('#')) { continue }
  $p = Start-Process $Exe -ArgumentList '--show', $name -PassThru
  Start-Sleep -Seconds $Seconds
  if ($p.HasExited) { Write-Host "PAGE FAIL: $name exited with code $($p.ExitCode)"; $failed++; continue }
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
  $bmp.Save("$Out/$name.png"); $bmp.Dispose()
  Stop-Process $p -Force -ErrorAction SilentlyContinue
  Write-Host "PAGE OK: $name"
}
if ($failed -gt 0) { throw "$failed page(s) failed to start" }
