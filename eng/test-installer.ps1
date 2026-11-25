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
    "--define=TestDocumentsDirectory=$documents" "--define=TestShellDirectory=$shell" "--define=TestDefaultDirectory=$app" `
    (Join-Path $PSScriptRoot 'installer/Sims4ModSieve.iss')
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

function Run-Setup([string]$Name, [string]$Target = $app, [bool]$Success = $true, [string]$PathCheckFailure = '') {
    $log = Join-Path $case "$Name.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/MERGETASKS=desktopicon', ('/LOG="' + $log + '"'))
    if ($Target) { $arguments += '/DIR="' + $Target + '"' }
    if ($PathCheckFailure) { $arguments += '/SIEVETESTPATHCHECK=' + $PathCheckFailure }
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    $runtimeErrors = Select-String -LiteralPath $log -Pattern 'Runtime error|Cannot call .* function|Internal error|内部错误' | Where-Object {
        -not ($PathCheckFailure -eq 'extract' -and $_.Line -match '解包失败：.*ExtractTemporaryFile: The file "missing-test-helper\.exe" was not found')
    }
    if ($runtimeErrors) { throw "$Name hit an installer runtime error. See $log" }
    if (($process.ExitCode -eq 0) -ne $Success) { throw "$Name returned $($process.ExitCode). See $case" }
    Write-Host "PASS $Name"
}

$pathCheckLog = Join-Path $data 'installer-check.log'
function Assert-PathCheckLog([string]$Log, [string]$Text) {
    if (-not (Select-String -LiteralPath $Log -Pattern $Text -SimpleMatch -Quiet)) { throw "Missing path-check detail '$Text' in $Log" }
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
    $tray = Join-Path $case 'CustomTray'
    $settings = Join-Path $data 'duplicate-settings.json'
    New-Item -ItemType Directory -Path $mods,$tray -Force | Out-Null
    $validSettings = @{ ModsRoot = $mods; Sources = @(); TrayRoot = $tray } | ConvertTo-Json
    Set-Content -LiteralPath $settings -Value $validSettings
    Run-Setup 'ConfiguredModsRejected' (Join-Path $mods 'Tool') $false
    Run-Setup 'ConfiguredTrayRejected' (Join-Path $tray 'Tool') $false
    Run-Setup 'DataDirectoryRejected' (Join-Path $data 'Tool') $false
    Run-Setup 'FreshInstall'
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $app $relative)).Hash) { throw "Installed payload differs: $relative" }
    }
    $cli = Join-Path $app 'Sims4ModSieve.Cli.exe'
    $helpText = & $cli scan-scripts --help
    if ($LASTEXITCODE -ne 0 -or -not ($helpText -match 'scan-scripts')) { throw 'Installed CLI help failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $app 'AI-GUIDE.md') -PathType Leaf)) { throw 'Installed guide is missing.' }
    $entry = Get-ItemProperty -LiteralPath $registration
    if ($entry.DisplayVersion -ne $version -or $entry.InstallLocation.TrimEnd('\') -ne $app) { throw 'Uninstall registration differs.' }
    if ($entry.UninstallString -ne ('"' + (Join-Path $app 'unins000.exe') + '" /SILENT /SIEVEINTERACTIVE=1') -or $entry.QuietUninstallString -like '*SIEVEINTERACTIVE*') { throw 'Interactive and silent uninstall registration differ.' }
    foreach ($folder in @('Start','Desktop')) {
        $target = [InstallerShortcutTarget]::Read((Join-Path $shell "$folder/Sims 4 Mod Sieve.lnk"))
        if ($target -ne (Join-Path $app 'Sims4ModSieve.exe')) { throw "Invalid $folder shortcut target: $target" }
    }
    Write-Host 'PASS PayloadRegistrationAndShortcuts'

    Set-Content -LiteralPath $settings -Value '{'
    $legacySettings = Join-Path (New-Item -ItemType Directory -Path (Join-Path $case 'Sim4ModSieve')).FullName 'duplicate-settings.json'
    Set-Content -LiteralPath $legacySettings -Value '{}'
    Run-Setup 'BadSettingsAllowInstall'
    Assert-PathCheckLog $pathCheckLog "跳过设置：$settings"
    Assert-PathCheckLog $pathCheckLog "跳过设置：$legacySettings"
    Run-Setup 'BadSettingsStillRejectGamePath' $game $false

    $savedDocuments = Join-Path $case 'Documents original'
    Move-Item -LiteralPath $documents -Destination $savedDocuments
    try {
        Set-Content -LiteralPath $documents -Value 'simulated unresolved documents directory'
        Run-Setup 'UnresolvedDocumentsAllowInstall'
        Assert-PathCheckLog $pathCheckLog "文档中的《模拟人生 4》文件夹：$documents\Electronic Arts\The Sims 4。解析失败"
    } finally {
        Remove-Item -LiteralPath $documents
        Move-Item -LiteralPath $savedDocuments -Destination $documents
    }

    $drives = [IO.DriveInfo]::GetDrives().Name
    $offlineDrive = 68..90 | ForEach-Object { "$([char]$_):\" } | Where-Object { $_ -notin $drives } | Select-Object -First 1
    if (-not $offlineDrive) { throw 'No unused drive letter for the offline-drive fixture.' }
    $offlineMods = "$($offlineDrive)Mods"
    $brokenLink = Join-Path $case 'BrokenModsLink'
    $brokenTarget = (New-Item -ItemType Directory -Path (Join-Path $case 'BrokenModsTarget')).FullName
    & $env:ComSpec /d /c mklink /J $brokenLink $brokenTarget
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Mods junction.' }
    try {
        [IO.Directory]::Delete($brokenTarget)
        @{ ModsRoot = $brokenLink; Sources = @(); TrayRoot = $tray } | ConvertTo-Json | Set-Content -LiteralPath $settings
        Run-Setup 'BrokenModsLinkAllowsInstall'
        $brokenDetail = [regex]::Escape("Mods：$brokenLink。解析失败，") + '.+' + [regex]::Escape("使用完整路径 $brokenLink")
        if (-not (Select-String -LiteralPath $pathCheckLog -Pattern $brokenDetail -Quiet)) { throw 'Broken junction did not log the failure and fallback path.' }
    } finally { [IO.Directory]::Delete($brokenLink) }
    @{ ModsRoot = $offlineMods; Sources = @(); TrayRoot = $tray } | ConvertTo-Json | Set-Content -LiteralPath $settings
    Run-Setup 'OfflineModsDriveAllowsInstall'
    Assert-PathCheckLog $pathCheckLog '所在的盘当前无法访问'
    Set-Content -LiteralPath $settings -Value $validSettings

    Run-Setup 'UnknownDefaultPathAllowsInstall' '' $true 'start'
    Assert-PathCheckLog $pathCheckLog '启动失败，Windows 错误'
    Run-Setup 'HelperExtractionFailureAllowsSilentInstall' $app $true 'extract'
    Assert-PathCheckLog $pathCheckLog '解包失败'
    Run-Setup 'UnexpectedHelperExitAllowsSilentInstall' $app $true 'exit'
    Assert-PathCheckLog $pathCheckLog '检查程序退出码：23'

    $savedLog = Join-Path $case 'installer-check-before-fallback.log'
    Move-Item -LiteralPath $pathCheckLog -Destination $savedLog
    try {
        New-Item -ItemType Directory -Path $pathCheckLog | Out-Null
        Run-Setup 'PathCheckLogUsesFallbackLocation' $app $true 'start'
        Assert-PathCheckLog (Join-Path $case 'Sims4ModSieve-installer-check.log') '启动失败，Windows 错误'
    } finally {
        [IO.Directory]::Delete($pathCheckLog)
        Move-Item -LiteralPath $savedLog -Destination $pathCheckLog
    }

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
    foreach ($pathCheckFailure in @('none', 'start')) {
        $child = [Diagnostics.Process]::Start($childStart)
        try {
            if ($child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() -ne 'READY') { throw 'Test child did not acquire running lock.' }
            if ($pathCheckFailure -eq 'none') {
                Run-Setup 'RunningAppBlocksManualInstall' $app $false
                Run-Uninstall 'RunningAppBlocksUninstall' $false
            }
            Set-Content -LiteralPath (Join-Path $app 'README.md') -Value 'wait for complete process exit'
            $handoff = Join-Path $case "handoff-$pathCheckFailure"
            $update = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
                ('/DIR="' + $app + '"'), ('/SIEVEHANDOFF="' + $handoff + '"'), ('/SIEVEWAITPID=' + $child.Id),
                ('/SIEVETESTPATHCHECK=' + $pathCheckFailure), ('/LOG="' + (Join-Path $case "handoff-$pathCheckFailure.log") + '"')) -WindowStyle Hidden -PassThru
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
        if ($pathCheckFailure -eq 'start') {
            Assert-PathCheckLog (Join-Path $case 'handoff-start.log') '启动失败，Windows 错误'
            Write-Host 'PASS UpdateHandoffContinuesWhenHelperCannotStart'
        } else { Write-Host 'PASS UpdateHandoffWaitsForNormalExit' }
    }
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
    if (Test-Path -LiteralPath $pathCheckLog -PathType Leaf) { Copy-Item -LiteralPath $pathCheckLog -Destination $diagnostics -Force }
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
