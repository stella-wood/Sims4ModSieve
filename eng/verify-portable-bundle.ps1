param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PackageDirectory 'Sims4ModSieve.exe'

# The pinned SDK writes bundle format 6. Read the actual EXE, not intermediate build files.
# Layout: https://github.com/dotnet/runtime/blob/v10.0.14/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
try {
    $signature = [byte[]]@(
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    )
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $signatureText = $encoding.GetString($signature)
    $headerOffset = [long]0
    $searchOffset = [long]0
    while ($searchOffset -lt $reader.BaseStream.Length) {
        $reader.BaseStream.Position = $searchOffset
        $chunk = $reader.ReadBytes(1MB)
        $index = $encoding.GetString($chunk).IndexOf($signatureText, [StringComparison]::Ordinal)
        if ($index -ge 0 -and $searchOffset + $index -ge 8) {
            $reader.BaseStream.Position = $searchOffset + $index - 8
            $headerOffset = $reader.ReadInt64()
            break
        }
        if ($chunk.Length -le 39) { break }
        $searchOffset += $chunk.Length - 39
    }
    if ($headerOffset -le 0 -or $headerOffset -ge $reader.BaseStream.Length) { throw 'Executable is not a single-file bundle.' }
    $reader.BaseStream.Position = $headerOffset
    $major = $reader.ReadUInt32()
    $minor = $reader.ReadUInt32()
    if ($major -ne 6 -or $minor -ne 0) { throw "Unsupported bundle format: $major.$minor" }
    $count = $reader.ReadInt32()
    $null = $reader.ReadString()
    $null = $reader.ReadInt64(); $null = $reader.ReadInt64()
    $runtimeOffset = $reader.ReadInt64()
    $runtimeSize = $reader.ReadInt64()
    if ($reader.ReadUInt64() -ne 0) { throw 'Bundle extraction compatibility mode must be disabled.' }
    $entries = @(
        for ($i = 0; $i -lt $count; $i++) {
            $offset = $reader.ReadInt64()
            $size = $reader.ReadInt64()
            $compressedSize = $reader.ReadInt64()
            $type = $reader.ReadByte()
            $path = $reader.ReadString()
            if ($offset -lt 0 -or $size -lt 0 -or $offset -gt $headerOffset - $size) { throw "Invalid bundle entry: $path" }
            if ($type -notin @(1, 3, 4) -or $compressedSize -ne 0) { throw "Unexpected extraction or compression entry: $path" }
            if ($path.Contains('/') -and ($path.Split('/')[0] -notin @('zh-Hans', 'en'))) { throw "Unexpected bundled resource language: $path" }
            [pscustomobject]@{ Path = $path; Type = $type }
        }
    )
    $requiredAssemblies = @(
        'Sims4ModSieve.dll', 'Sims4ModSieve.Core.dll', 'Sims4ModSieve.Packages.dll',
        'System.Private.CoreLib.dll', 'PresentationFramework.dll',
        'LlamaLogic.Packages.dll', 'ICSharpCode.SharpZipLib.dll', 'BCnEncoder.dll',
        'StbImageSharp.dll', 'StbImageWriteSharp.dll', 'YamlDotNet.dll'
    )
    foreach ($name in $requiredAssemblies) {
        if (@($entries | Where-Object { $_.Path -eq $name -and $_.Type -eq 1 }).Count -ne 1) { throw "Missing bundled assembly: $name" }
    }
    if (@($entries | Where-Object { $_.Path -eq 'Sims4ModSieve.runtimeconfig.json' -and $_.Type -eq 4 }).Count -ne 1 -or
        @($entries | Where-Object { $_.Path -eq 'Sims4ModSieve.deps.json' -and $_.Type -eq 3 }).Count -ne 1) { throw 'Missing bundled runtime configuration or dependency manifest.' }
    if ($runtimeOffset -lt 0 -or $runtimeSize -le 0 -or $runtimeSize -gt 1MB -or $runtimeOffset -gt $headerOffset - $runtimeSize) { throw 'Invalid bundled runtime configuration.' }
    $reader.BaseStream.Position = $runtimeOffset
    $runtime = [Text.Encoding]::UTF8.GetString($reader.ReadBytes([int]$runtimeSize)) | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -ne 2 -or @($frameworks | Where-Object { $_.name -in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') -and $_.version -eq '10.0.14' }).Count -ne 2) { throw 'Unexpected bundled .NET runtime version.' }
} finally { $reader.Dispose() }

foreach ($file in Get-ChildItem -LiteralPath $PackageDirectory -File) {
    if ($file.Extension -eq '.dll') {
        $stream = [IO.File]::OpenRead($file.FullName)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try { if ($pe.HasMetadata) { throw "Managed DLL outside bundle: $($file.Name)" } }
        finally { $pe.Dispose(); $stream.Dispose() }
    } elseif ($file.Extension -in @('.json', '.pdb')) { throw "Unexpected loose publish file: $($file.Name)" }
}
foreach ($directory in Get-ChildItem -LiteralPath $PackageDirectory -Directory) {
    if ($directory.Name -notin @('Assets', 'licenses')) { throw "Unexpected top-level directory: $($directory.Name)" }
}
Write-Host "Verified $count uncompressed bundle entries; .NET 10.0.14; native libraries remain outside; no extraction entries."
