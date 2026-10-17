# Fills the winget and scoop manifests in packaging\ with the version and the SHA-256 values of a release, from the SHA256SUMS.txt that build\checksums.ps1 wrote.
# It only edits files; submitting them to winget-pkgs or a scoop bucket is a manual step (docs\updates.md).
#   powershell -ExecutionPolicy Bypass -File build\update-manifests.ps1 -Version 0.5.0 -SumsFile publish\SHA256SUMS.txt [-Packaging packaging]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SumsFile,
    [string]$Packaging = (Join-Path (Split-Path $PSScriptRoot -Parent) "packaging")
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (a release, not a pre-release): $Version" }

$sums = @{}
foreach ($line in Get-Content $SumsFile) {
    if ($line -match '^([0-9a-fA-F]{64}) [ *](\S+)$') { $sums[$Matches[2]] = $Matches[1].ToLowerInvariant() }
}
function HashOf($name) {
    if (-not $sums.ContainsKey($name)) { throw "$name is not listed in $SumsFile" }
    return $sums[$name]
}
function Rewrite($path, [scriptblock]$edit) {
    $text = [System.IO.File]::ReadAllText($path)
    $text = & $edit $text
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
}
# Replaces the version everywhere, then the hash that follows each installer URL.
function FillInstallers($text, $hashKey) {
    $text = $text -replace '(?<=download/v)\d+\.\d+\.\d+(?=/)', $Version
    $text = $text -replace '(?<=Setup-)\d+\.\d+\.\d+(?=-(x64|arm64)\.exe)', $Version
    $pattern = "(?<name>Remotely-(?:Server-)?Setup-$([regex]::Escape($Version))-(?:x64|arm64)\.exe)(?<mid>[`"']?,?\s*\r?\n\s*[`"']?$hashKey[`"']?\s*:\s*[`"']?)(?<hash>[^`"'\r\n,]+)"
    return [regex]::Replace($text, $pattern, { param($m) $m.Groups['name'].Value + $m.Groups['mid'].Value + (HashOf $m.Groups['name'].Value) })
}

foreach ($id in "Burim13.Remotely", "Burim13.RemotelyServer") {
    foreach ($file in Get-ChildItem (Join-Path $Packaging "winget\$id") -Filter "*.yaml") {
        Rewrite $file.FullName {
            param($t)
            $t = $t -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
            if ($file.Name -like "*.installer.yaml") { $t = FillInstallers $t 'InstallerSha256' }
            $t
        }
    }
}
Rewrite (Join-Path $Packaging "scoop\remotely.json") {
    param($t)
    $t = $t -replace '(?m)^(\s*"version"\s*:\s*")[^"]*(")', "`${1}$Version`${2}"
    FillInstallers $t 'hash'
}
Write-Host "Manifests in $Packaging now describe version $Version."
