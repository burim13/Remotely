# Writes SHA256SUMS.txt next to the installers: one line per file, "<64 hex characters>  <file name>" (the format of sha256sum), UTF-8 without BOM, LF line ends.
# This is the file the update check in the apps reads before it offers a download (src/Remotely.Core/Update). Run it after the installers are signed, because
# signing changes the file. Attach SHA256SUMS.txt to the release together with the installers.
#   powershell -ExecutionPolicy Bypass -File build\checksums.ps1 -Dir publish
param([string]$Dir = "publish")
$ErrorActionPreference = "Stop"
$files = Get-ChildItem -Path $Dir -Filter "Remotely-*Setup-*.exe" | Sort-Object Name
if (-not $files) { throw "No installers (Remotely-*Setup-*.exe) found in $Dir" }
$lines = foreach ($f in $files) { "{0}  {1}" -f (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $f.Name }
$target = Join-Path (Resolve-Path $Dir) "SHA256SUMS.txt"
[System.IO.File]::WriteAllText($target, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))
Write-Host "wrote $target"
$lines | ForEach-Object { Write-Host "  $_" }
