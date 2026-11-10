[CmdletBinding()]
param([switch]$UseExistingPublish)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj'
$versionOutput = & "$PSScriptRoot/dotnet.cmd" msbuild $project -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not read the project version.' }
$version = ($versionOutput -join "`n").Trim()
if ($version -notmatch '^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw "Invalid package version: $version" }
$fileVersion = $Matches[1] + '.0'
$releaseDirectory = Join-Path $root 'artifacts/release'
$source = Join-Path $releaseDirectory "Sims4ModSieve-$version-win-x64"
if (-not $UseExistingPublish) { & "$PSScriptRoot/package-portable.ps1" -SkipArchive }
& "$PSScriptRoot/verify-portable-bundle.ps1" -PackageDirectory $source
& "$PSScriptRoot/install-inno-setup.ps1"
$compiler = Join-Path $root '.tools/inno-setup/compiler/ISCC.exe'
& $compiler '--quiet' "--define=AppVersion=$version" "--define=FileVersion=$fileVersion" "--define=SourceDirectory=$source" "--define=OutputDirectory=$releaseDirectory" (Join-Path $PSScriptRoot 'installer/Sims4ModSieve.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $releaseDirectory "Sims4ModSieve-$version-setup.exe"
if ((Get-Item -LiteralPath $setup).VersionInfo.ProductVersion.Trim() -ne $version) { throw 'Installer version does not match the project.' }
(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
Write-Host "Installer: $setup"
