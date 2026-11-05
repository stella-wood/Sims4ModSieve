$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$verify = Join-Path $workspace 'eng/verify-portable-bundle.ps1'
$source = Join-Path $workspace 'artifacts/release/Sims4ModSieve-0.1.0-alpha.1-win-x64'
$evidenceDirectory = Join-Path $workspace 'artifacts/portable-bundle-tests'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$case = Join-Path $evidenceDirectory ([Guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $source -Destination $case -Recurse
$exe = Join-Path $case 'Sims4ModSieve.exe'
function Reject([string]$Name, [string]$Expected) {
    try { & $verify -PackageDirectory $case; throw "Did not reject $Name" }
    catch { if ($_.Exception.Message -notlike "*$Expected*") { throw }; Write-Host "PASS $Name" }
}
. $verify -PackageDirectory $case
Write-Host 'PASS ValidBundle'
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
try {
    $reader.BaseStream.Position = $headerOffset + 12
    $null = $reader.ReadString()
    $null = $reader.ReadInt64(); $null = $reader.ReadInt64()
    $runtimeOffset = $reader.ReadInt64(); $runtimeSize = $reader.ReadInt64()
    $flagsOffset = $reader.BaseStream.Position
    $null = $reader.ReadUInt64()
    $compressionOffset = $reader.BaseStream.Position + 16
    $typeOffset = $reader.BaseStream.Position + 24
    $reader.BaseStream.Position = $typeOffset
    $originalType = $reader.ReadByte()
    $reader.BaseStream.Position = $runtimeOffset
    $runtimeBytes = $reader.ReadBytes([int]$runtimeSize)
} finally { $reader.Dispose() }
function Patch([long]$Offset, [byte[]]$Bytes) {
    $stream = [IO.File]::OpenWrite($exe)
    try { $stream.Position = $Offset; $stream.Write($Bytes) } finally { $stream.Dispose() }
}
Copy-Item -LiteralPath (Join-Path $workspace 'src/Sims4ModSieve.Core/bin/Release/net10.0/Sims4ModSieve.Core.dll') -Destination (Join-Path $case 'leaked.dll')
try { Reject 'LooseManagedDllRejected' 'Managed DLL outside bundle' } finally { Remove-Item -LiteralPath (Join-Path $case 'leaked.dll') }
foreach ($extension in @('json', 'pdb')) {
    $extra = Join-Path $case "leaked.$extension"
    Set-Content -LiteralPath $extra -Value 'test'
    try { Reject "Loose${extension}Rejected" 'Unexpected loose publish file' } finally { Remove-Item -LiteralPath $extra }
}
$language = New-Item -ItemType Directory -Path (Join-Path $case 'de')
try { Reject 'UnwantedLanguageDirectoryRejected' 'Unexpected top-level directory' } finally { Remove-Item -LiteralPath $language.FullName }
Patch $flagsOffset ([BitConverter]::GetBytes([ulong]1))
try { Reject 'ExtractionModeRejected' 'extraction compatibility mode' } finally { Patch $flagsOffset ([byte[]]::new(8)) }
Patch $compressionOffset ([BitConverter]::GetBytes([long]1))
try { Reject 'CompressedPayloadRejected' 'Unexpected extraction or compression entry' } finally { Patch $compressionOffset ([byte[]]::new(8)) }
Patch $typeOffset ([byte[]]@(2))
try { Reject 'NativePayloadRejected' 'Unexpected extraction or compression entry' } finally { Patch $typeOffset ([byte[]]@($originalType)) }
$incorrect = [Text.Encoding]::UTF8.GetBytes([Text.Encoding]::UTF8.GetString($runtimeBytes).Replace('10.0.14', '10.0.13'))
Patch $runtimeOffset $incorrect
try { Reject 'WrongRuntimeRejected' 'Unexpected bundled .NET runtime version' } finally { Patch $runtimeOffset $runtimeBytes }
$backup = Join-Path $case 'Sims4ModSieve.saved'
Copy-Item -LiteralPath $exe -Destination $backup
try {
    [IO.File]::WriteAllBytes($exe, [Text.Encoding]::UTF8.GetBytes('not a bundle'))
    Reject 'NonBundleRejected' 'Executable is not a single-file bundle'
} finally {
    Copy-Item -LiteralPath $backup -Destination $exe -Force
    Remove-Item -LiteralPath $backup
}
& $verify -PackageDirectory $case
Write-Host 'PASS RestoredBundle'
