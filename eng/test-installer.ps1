[CmdletBinding()]
param([switch]$ReviewUi)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versionOutput = & "$PSScriptRoot/dotnet.cmd" msbuild (Join-Path $root 'src/Sims4ModSieve.Desktop/Sims4ModSieve.Desktop.csproj') -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not read the project version.' }
$version = ($versionOutput -join "`n").Trim()
$fileVersion = ($version -split '[-+]')[0] + '.0'
$source = Join-Path $root "artifacts/release/Sims4ModSieve-$version-win-x64"
$testsDirectory = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/installer-tests'))
$case = Join-Path $testsDirectory ([Guid]::NewGuid().ToString('N'))
$data = Join-Path $case 'Sims4ModSieve'
$documents = Join-Path $case 'Documents'
$app = Join-Path $case 'App 测试目录'
$shell = Join-Path $case 'Shell'
New-Item -ItemType Directory -Path $case,$data,$documents -Force | Out-Null
$compiler = Join-Path $root '.tools/inno-setup/compiler/ISCC.exe'
& $compiler '--quiet' '--no-compression' "--define=AppVersion=$version" "--define=FileVersion=$fileVersion" `
    "--define=SourceDirectory=$source" "--define=OutputDirectory=$case" "--define=TestDataDirectory=$data" `
    "--define=TestDocumentsDirectory=$documents" "--define=TestShellDirectory=$shell" (Join-Path $PSScriptRoot 'installer/Sims4ModSieve.iss')
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
$setup = Join-Path $case "Sims4ModSieve-$version-setup.exe"
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C897868A-0997-463F-B377-A68A616F831E}-InstallerTests_is1'

# 使用 Unicode Shell Link 接口核对中文目标路径，避免依赖系统 ANSI 代码页。
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public static class InstallerShortcutTarget {
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags); }
    public static string Read(string path) {
        var link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true));
        try {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 4);
            return target.ToString();
        } finally { Marshal.FinalReleaseComObject(link); }
    }
}
'@

function Run-Setup([string]$Name, [string]$Target = $app, [bool]$Success = $true) {
    $log = Join-Path $case "$Name.log"
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        '/MERGETASKS=desktopicon', ('/DIR="' + $Target + '"'), ('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru -Wait
    if (Select-String -LiteralPath $log -Pattern 'Runtime error|Cannot call .* function|Internal error|内部错误' -Quiet) { throw "$Name hit an installer runtime error. See $log" }
    if (($process.ExitCode -eq 0) -ne $Success) { throw "$Name returned $($process.ExitCode). See $case" }
    Write-Host "PASS $Name"
}

function Run-Uninstall([string]$Name, [bool]$Success = $true) {
    $log = Join-Path $case "$Name.log"
    $process = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru -Wait
    if (Select-String -LiteralPath $log -Pattern 'Runtime error|Cannot call .* function|Internal error|内部错误' -Quiet) { throw "$Name hit an uninstaller runtime error. See $log" }
    if (($process.ExitCode -eq 0) -ne $Success) { throw "$Name returned $($process.ExitCode). See $case" }
    Write-Host "PASS $Name"
}

try {
    $game = Join-Path $documents 'Electronic Arts/The Sims 4/Mods/Tool'
    Run-Setup 'GamePathRejected' $game $false
    if (Test-Path -LiteralPath (Join-Path $game 'Sims4ModSieve.exe')) { throw 'Installed into game directory.' }
    $mods = Join-Path $case 'CustomMods'
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    @{ ModsRoot = $mods; Sources = @(); TrayRoot = $null } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'duplicate-settings.json')
    Run-Setup 'ConfiguredModsRejected' (Join-Path $mods 'Tool') $false
    Run-Setup 'FreshInstall'
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $app $relative)).Hash) { throw "Installed payload differs: $relative" }
    }
    $entry = Get-ItemProperty -LiteralPath $registration
    if ($entry.DisplayVersion -ne $version -or $entry.InstallLocation.TrimEnd('\') -ne $app) { throw 'Uninstall registration differs.' }
    if ($entry.UninstallString -ne ('"' + (Join-Path $app 'unins000.exe') + '" /SILENT /SIEVEINTERACTIVE=1') -or $entry.QuietUninstallString -like '*SIEVEINTERACTIVE*') { throw 'Interactive and silent uninstall registration differ.' }
    foreach ($folder in @('Start','Desktop')) {
        $target = [InstallerShortcutTarget]::Read((Join-Path $shell "$folder/Sims 4 Mod Sieve.lnk"))
        if ($target -ne (Join-Path $app 'Sims4ModSieve.exe')) { throw "Invalid $folder shortcut target: $target" }
    }
    Write-Host 'PASS PayloadRegistrationAndShortcuts'
    $unknown = Join-Path $app 'user-note.txt'
    Set-Content -LiteralPath $unknown -Value 'keep user file'
    $settingsBefore = Get-Content -LiteralPath (Join-Path $data 'duplicate-settings.json') -Raw
    Set-Content -LiteralPath (Join-Path $app 'README.md') -Value 'previous version'
    Run-Setup 'CoverInstall'
    if ((Get-FileHash -LiteralPath (Join-Path $app 'README.md')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $source 'README.md')).Hash) { throw 'Upgrade did not replace program content.' }
    if ((Get-Content -LiteralPath (Join-Path $data 'duplicate-settings.json') -Raw) -ne $settingsBefore) { throw 'Upgrade changed settings.' }
    $lockPath = (Join-Path $data 'application.lock').Replace("'", "''")
    $childCommand = @"
`$stream = [IO.File]::Open('$lockPath', 'OpenOrCreate', 'ReadWrite', 'None')
[Console]::Out.WriteLine('READY')
[Console]::In.ReadLine() | Out-Null
`$stream.Dispose()
[Console]::Out.WriteLine('RELEASED')
[Console]::In.ReadLine() | Out-Null
"@
    $childStart = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $childStart.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
    $childStart.UseShellExecute = $false
    $childStart.CreateNoWindow = $true
    $childStart.WindowStyle = 'Hidden'
    $childStart.RedirectStandardInput = $true
    $childStart.RedirectStandardOutput = $true
    $child = [Diagnostics.Process]::Start($childStart)
    try {
        if ($child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() -ne 'READY') { throw 'Test child did not acquire running lock.' }
        Run-Setup 'RunningAppBlocksManualInstall' $app $false
        Run-Uninstall 'RunningAppBlocksUninstall' $false
        Set-Content -LiteralPath (Join-Path $app 'README.md') -Value 'wait for complete process exit'
        $handoff = Join-Path $case 'handoff'
        $update = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
            ('/DIR="' + $app + '"'), ('/SIEVEHANDOFF="' + $handoff + '"'), ('/SIEVEWAITPID=' + $child.Id),
            ('/LOG="' + (Join-Path $case 'handoff.log') + '"')) -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $handoff)) {
            if ($update.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Installer did not acknowledge handoff.' }
            Start-Sleep -Milliseconds 100
        }
        if ($update.HasExited) { throw 'Installer did not wait for running app.' }
        [IO.File]::WriteAllText($handoff + '.accepted', $version, [Text.UTF8Encoding]::new($false))
        $child.StandardInput.WriteLine()
        if ($child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() -ne 'RELEASED') { throw 'Test child did not release running lock.' }
        Start-Sleep -Milliseconds 300
        if ((Get-Content -LiteralPath (Join-Path $app 'README.md')).Trim() -ne 'wait for complete process exit') { throw 'Update replaced files before the original process exited.' }
        $child.StandardInput.WriteLine()
        if (-not $child.WaitForExit(10000)) { throw 'Test child did not exit.' }
    } finally {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    if (-not $update.WaitForExit(30000) -or $update.ExitCode -ne 0) { throw 'Handoff installation failed.' }
    Write-Host 'PASS UpdateHandoffWaitsForNormalExit'
    $ledger = Join-Path $data 'disabled-mods.json'
    $disabled = Join-Path $mods 'test.package.off'
    Set-Content -LiteralPath $disabled -Value 'temporary mod'
    @(@{ Id = [Guid]::NewGuid().ToString('N'); OriginalPath = $disabled.Substring(0, $disabled.Length - 4); Size = 14;
        LastWriteTimeUtc = [DateTime]::UtcNow; DisabledAtUtc = [DateTime]::UtcNow }) | ConvertTo-Json -AsArray | Set-Content -LiteralPath $ledger
    $ledgerBefore = Get-Content -LiteralPath $ledger -Raw
    Run-Uninstall 'DisabledModsBlockUninstall' $false
    if ((Get-Content -LiteralPath $ledger -Raw) -ne $ledgerBefore -or -not (Test-Path -LiteralPath $disabled)) { throw 'Uninstall altered disabled mod state.' }
    Set-Content -LiteralPath $ledger -Value '{'
    Run-Uninstall 'UnreadableLedgerBlocksUninstall' $false
    Set-Content -LiteralPath $ledger -Value '[]'
    Run-Uninstall 'UninstallPreservesDataAndUnmanagedFiles'
    if (-not (Test-Path -LiteralPath $unknown) -or -not (Test-Path -LiteralPath $ledger)) { throw 'Uninstall deleted user files or data.' }
    if (Test-Path -LiteralPath $registration) { throw 'Uninstall registration remains.' }
    if (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe')) { throw 'Program remains after uninstall.' }
    if ((Test-Path -LiteralPath $shell) -and (Get-ChildItem -LiteralPath $shell -Filter '*.lnk' -Recurse)) { throw 'Shortcuts remain after uninstall.' }
    if ($ReviewUi) { & "$PSScriptRoot/test-installer-ui.ps1" -TestDirectory $case }
    Write-Host "Installer test evidence: $case"
} finally {
    $diagnostics = Join-Path $root 'artifacts/ci/installer'
    New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
    Get-ChildItem -LiteralPath $case -Filter '*.log' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $diagnostics -Force }
    $screenshots = Join-Path $root 'artifacts/installer-review'
    if ($ReviewUi -and (Test-Path -LiteralPath $screenshots)) {
        $imageDiagnostics = Join-Path $diagnostics 'screenshots'
        New-Item -ItemType Directory -Path $imageDiagnostics -Force | Out-Null
        Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $imageDiagnostics -Force }
    }
    # 仅清理这个隔离 AppId；失败时保留文件与日志供排查。
    if ((Test-Path -LiteralPath $registration) -and (Test-Path -LiteralPath (Join-Path $app 'unins000.exe'))) {
        Set-Content -LiteralPath (Join-Path $data 'disabled-mods.json') -Value '[]'
        Run-Uninstall 'CleanupTestInstallation'
    }
}
