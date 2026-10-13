# Signs a file with a code-signing certificate from the current user's store (Authenticode, SHA-256, timestamped). Same approach as Portico's build\sign.ps1.
#   powershell -ExecutionPolicy Bypass -File build\sign.ps1 -Path publish\Remotely-Setup-0.2.0-arm64.exe
# A self-made certificate names the publisher and proves the file is untouched, but SmartScreen still warns on downloaded files because it has no reputation.
# Make one (once):  New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Remotely" -CertStoreLocation Cert:\CurrentUser\My
param([Parameter(Mandatory = $true)][string]$Path, [string]$Subject = "Remotely")
$ErrorActionPreference = "Stop"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -like "*CN=$Subject*" } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { Write-Host "No code-signing certificate for '$Subject' in the current user's store: leaving $Path unsigned."; return }
$result = $null
foreach ($ts in @("http://timestamp.digicert.com", "http://timestamp.sectigo.com")) {
    try { $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $ts -IncludeChain All; if ($result.Status -in "Valid", "UnknownError") { break } } catch { $result = $null }
}
if (-not $result) { $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256 -IncludeChain All }
# "UnknownError" means "signed, but the chain is not trusted on this PC": expected for a self-made certificate.
"signed {0}: {1} ({2})" -f (Split-Path $Path -Leaf), $result.Status, $cert.Subject.Split(',')[0]
