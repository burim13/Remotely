# Builds the Windows UI test runner (tests/Remotely.UITests) once and prints the path of its exe. Needs the .NET SDK on a Windows machine.
#   $uitests = ./tools/ci/build-uitests.ps1            # builds out/uitests when it is not there yet
param([string]$Out = "out/uitests")
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$exe = Join-Path $root "$Out/Remotely.UITests.exe"
if (-not (Test-Path $exe)) {
  dotnet publish (Join-Path $root 'tests/Remotely.UITests') -c Release -o (Join-Path $root $Out) | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "building tests/Remotely.UITests failed" }
}
$exe
