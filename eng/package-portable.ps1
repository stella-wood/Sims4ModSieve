[CmdletBinding()]
param([switch]$SkipArchive)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj'
$versionOutput = & "$PSScriptRoot/dotnet.cmd" msbuild $project -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not read the project version.' }
$Version = ($versionOutput -join "`n").Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw "Invalid package version: $Version" }
$releaseDir = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/release'))
$out = [IO.Path]::GetFullPath((Join-Path $releaseDir "Sims4ModSieve-$Version-win-x64"))
$zip = "$out.zip"
if ((Split-Path $out -Parent) -ne $releaseDir -or (Split-Path $zip -Parent) -ne $releaseDir -or -not $out.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package target is outside artifacts/release.' }
foreach ($target in @((Join-Path $root 'artifacts'), $releaseDir, $out, $zip)) { if ((Test-Path $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $target" } }
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
& "$PSScriptRoot/dotnet.cmd" restore $project --locked-mode --nologo -m:1 -p:RuntimeIdentifier=win-x64 -p:PublishProfile=PortableWinX64
if ($LASTEXITCODE -ne 0) { throw 'Locked RID restore failed.' }
& "$PSScriptRoot/check-vulnerabilities.ps1"
& "$PSScriptRoot/dotnet.cmd" publish $project -c Release --no-restore --nologo -m:1 -p:RuntimeIdentifier=win-x64 -p:PublishProfile=PortableWinX64 -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$required = @(
    'Sims4ModSieve.exe', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll',
    'PenImc_cor3.dll', 'D3DCompiler_47_cor3.dll', 'vcruntime140_cor3.dll',
    'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Assets/Fonts/source-han-sans-LICENSE.txt',
    'licenses/LlamaLogic.Packages-4.9.0-LICENSE.txt',
    'licenses/SharpZipLib-1.4.2-LICENSE.txt',
    'licenses/BCnEncoder.Net-2.2.0-LICENSE-MIT.txt',
    'licenses/StbImageSharp-2.30.16-UNLICENSE.txt',
    'licenses/StbImageWriteSharp-1.16.7-README-LICENSE.txt',
    'licenses/YamlDotNet-16.3.0-LICENSE.txt',
    'licenses/InnoSetup-7.1.1-LICENSE.txt',
    'licenses/dotnet-runtime-10.0.14/LICENSE.TXT',
    'licenses/dotnet-runtime-10.0.14/THIRD-PARTY-NOTICES.TXT',
    'licenses/dotnet-runtime-10.0.14/WINDOWSDESKTOP-LICENSE.txt'
)
foreach ($item in $required) { if (-not (Test-Path (Join-Path $out $item))) { throw "Missing package file: $item" } }
& "$PSScriptRoot/verify-portable-bundle.ps1" -PackageDirectory $out
$commit = (git -C $root rev-parse HEAD).Trim()
$dirty = [bool](git -C $root status --porcelain)
"Sims 4 Mod Sieve $Version`nSourceCommit: $commit`nDirty: $dirty`nRuntime: .NET 10.0.14 self-contained win-x64`nLayout: single-file managed assemblies; native libraries beside executable; no bundle extraction`nSatelliteResourceLanguages: zh-Hans;en" | Set-Content -LiteralPath (Join-Path $out 'BUILD-INFO.txt') -Encoding utf8
$manifest = Get-ChildItem $out -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\', '/')
    "$(($_ | Get-FileHash -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
}
$manifest | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
if ($SkipArchive) { Write-Host "Publish directory: $out"; return }
Compress-Archive -LiteralPath $out -DestinationPath $zip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
    $packageName = Split-Path $out -Leaf
    $files = @(Get-ChildItem $out -Recurse -File | ForEach-Object { "$packageName/" + [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\', '/') })
    if ($names.Count -ne $files.Count -or @($files | Where-Object { $_ -notin $names }).Count) { throw 'ZIP contents do not match publish directory.' }
} finally { $archive.Dispose() }
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Host "Package: $zip"
