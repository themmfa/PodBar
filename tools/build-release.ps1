# Builds the release files into dist\:
#   PodBar-Setup.exe  - self-contained installer/app (runs on any 64-bit Windows 10/11, no .NET needed)
# Usage: powershell -File tools\build-release.ps1
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $projectDir 'dist'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish (Join-Path $projectDir 'PodBar.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $dist -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The same exe installs itself when it is started under a "...Setup.exe" name.
Rename-Item (Join-Path $dist 'PodBar.exe') 'PodBar-Setup.exe'

Get-ChildItem $dist | ForEach-Object { "{0}  {1:N1} MB  SHA256 {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
