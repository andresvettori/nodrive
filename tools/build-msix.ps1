# Builds the unsigned MSIX for the Microsoft Store (the Store signs it). Run on Windows with the Windows SDK (makeappx).
# Identity values come from Partner Center > Product identity; defaults are placeholders that Store will reject.
param(
  [Parameter(Mandatory)] [string] $EngineDir,                 # folder with the published NoDrive.Engine.exe
  [Parameter(Mandatory)] [string] $Version,                   # 4-part, e.g. 0.9.149.0
  [Parameter(Mandatory)] [string] $Output,
  [string] $IdentityName = $env:MSIX_IDENTITY_NAME,
  [string] $Publisher = $env:MSIX_PUBLISHER,
  [string] $PublisherDisplay = $env:MSIX_PUBLISHER_DISPLAY
)
$ErrorActionPreference = 'Stop'
if (-not $IdentityName) { $IdentityName = 'NoDrive.Placeholder' }
if (-not $Publisher) { $Publisher = 'CN=Placeholder' }
if (-not $PublisherDisplay) { $PublisherDisplay = 'NoDrive' }
$stage = Join-Path ([IO.Path]::GetTempPath()) "nodrive-msix-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$EngineDir/NoDrive.Engine.exe" $stage
Copy-Item -Recurse "$PSScriptRoot/../installer/msix/Assets" "$stage/Assets"
(Get-Content "$PSScriptRoot/../installer/msix/AppxManifest.xml" -Raw).Replace('@IDENTITY_NAME@', $IdentityName).Replace('@PUBLISHER@', $Publisher).Replace('@PUBLISHER_DISPLAY@', $PublisherDisplay).Replace('@VERSION@', $Version) | Set-Content "$stage/AppxManifest.xml" -Encoding UTF8
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw 'makeappx.exe not found (install the Windows SDK)' }
& $makeappx.FullName pack /d $stage /p $Output /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
"$Output $((Get-Item $Output).Length) bytes (identity $IdentityName, $Publisher)"
