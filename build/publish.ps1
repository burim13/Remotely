# Builds everything and the installers into .\publish, on a Windows PC with the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#   powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 0.4.0]
# Output: Remotely-Setup-<version>-arm64.exe, Remotely-Setup-<version>-x64.exe, Remotely-Server-Setup-<version>-x64.exe (signed if build\sign.ps1 finds your certificate)
# and SHA256SUMS.txt (the file the update check reads; attach it to the release next to the installers).
param([string]$Version = "0.4.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
New-Item -ItemType Directory publish | Out-Null

foreach ($arch in @("arm64", "x64")) {
    $rid = "win-$arch"
    Write-Host "== publish $rid"
    # The WinUI 3 app is a folder (self-contained Windows App SDK), not a single file.
    dotnet publish src\Remotely.App -c Release -r $rid -p:Platform=$($arch.ToUpper() -replace 'X64','x64') -p:Version=$Version -o "publish\$arch\app"
    if ($LASTEXITCODE -ne 0) { throw "app publish failed for $rid" }
    dotnet publish src\Remotely.Host -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Version=$Version -o "publish\$arch\host"
    if ($LASTEXITCODE -ne 0) { throw "host publish failed for $rid" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path "publish\$arch\app\Remotely.exe"
    & powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path "publish\$arch\host\Remotely.Host.exe"
    Write-Host "== Inno Setup $arch"
    & $iscc "/DArch=$arch" "/DVersion=$Version" "/Qp" installer\Remotely.iss
    if ($LASTEXITCODE -ne 0) { throw "installer failed for $arch" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path "publish\Remotely-Setup-$Version-$arch.exe"
}

Write-Host "== publish server (x64)"
dotnet publish src\Remotely.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o publish\server
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }
& powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path publish\server\Remotely.Server.exe
Write-Host "== publish Server Manager (x64; the server installer carries it)"
dotnet publish src\Remotely.ServerManager -c Release -r win-x64 -p:Platform=x64 -p:Version=$Version -o publish\manager
if ($LASTEXITCODE -ne 0) { throw "server manager publish failed" }
& powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path publish\manager\Remotely.ServerManager.exe
& $iscc "/DVersion=$Version" "/Qp" installer\Remotely-Server.iss
if ($LASTEXITCODE -ne 0) { throw "server installer failed" }
& powershell -NoProfile -ExecutionPolicy Bypass -File build\sign.ps1 -Path "publish\Remotely-Server-Setup-$Version-x64.exe"

& powershell -NoProfile -ExecutionPolicy Bypass -File build\checksums.ps1 -Dir publish
Get-ChildItem "publish\Remotely-*Setup-*.exe" | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
