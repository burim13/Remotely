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
  $res = Join-Path (Resolve-Path $Out) "$name.result.txt"
  $p = Start-Process $Exe -ArgumentList '--show', $name, '--show-result', $res -PassThru
  Start-Sleep -Seconds $Seconds
  if ($p.HasExited) { Write-Host "PAGE FAIL: $name exited with code $($p.ExitCode)"; $failed++; continue }
  $r = if (Test-Path $res) { (Get-Content $res -TotalCount 1) } else { '(no result file)' }
  Write-Host "RESULT $name : $r"
  if ($r -notmatch "^LOADED $name \d+x\d+$") { Write-Host "PAGE FAIL: $name result line wrong"; $failed++; Stop-Process $p -Force -ErrorAction SilentlyContinue; continue }
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
  $bmp.Save("$Out/$name.png"); $bmp.Dispose()
  Stop-Process $p -Force -ErrorAction SilentlyContinue
  Write-Host "PAGE OK: $name"
}
# negative proof: a page that does not exist must exit with code 3 and say FAILED
$res = Join-Path (Resolve-Path $Out) 'NoSuchPage.result.txt'
$p = Start-Process $Exe -ArgumentList '--show', 'NoSuchPage', '--show-result', $res -PassThru
if (-not $p.WaitForExit(30000)) { $p.Kill(); Write-Host 'NEGATIVE FAIL: did not exit'; $failed++ }
else {
  $r = if (Test-Path $res) { (Get-Content $res -TotalCount 1) } else { '(no result file)' }
  Write-Host "NEGATIVE exit=$($p.ExitCode) result=$r"
  if ($p.ExitCode -ne 3 -or $r -notmatch '^FAILED NoSuchPage ') { Write-Host 'NEGATIVE FAIL'; $failed++ }
}
# shell self-test: second launch forwards --invite to the first instance
$sf = Join-Path (Resolve-Path $Out) 'selftest-shell.txt'
$p = Start-Process $Exe -ArgumentList '--selftest-shell', $sf -PassThru
if (-not $p.WaitForExit(90000)) { $p.Kill(); Write-Host 'SHELLTEST timed out' }
if (Test-Path $sf) { Get-Content $sf } else { Write-Host 'SHELLTEST no result file' }
if (-not (Test-Path $sf) -or (Get-Content $sf -TotalCount 1) -ne 'PASS') { Write-Host 'SHELLTEST FAIL'; $failed++ }
if ($failed -gt 0) { throw "$failed page(s) failed to start" }
