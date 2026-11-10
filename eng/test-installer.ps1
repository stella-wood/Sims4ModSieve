[CmdletBinding()]
param()

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

function Run-Setup([string]$Name, [string]$Target = $app, [bool]$Success = $true) {
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        '/MERGETASKS=desktopicon', ('/DIR="' + $Target + '"'), ('/LOG="' + (Join-Path $case "$Name.log") + '"')) -WindowStyle Hidden -PassThru -Wait
    if (($process.ExitCode -eq 0) -ne $Success) { throw "$Name returned $($process.ExitCode). See $case" }
    Write-Host "PASS $Name"
}

function Run-Uninstall([string]$Name, [bool]$Success = $true) {
    $process = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/LOG="' + (Join-Path $case "$Name.log") + '"')) -WindowStyle Hidden -PassThru -Wait
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
    $shortcutService = New-Object -ComObject WScript.Shell
    foreach ($folder in @('Start','Desktop')) {
        $shortcut = $shortcutService.CreateShortcut((Join-Path $shell "$folder/Sims 4 Mod Sieve.lnk"))
        if ($shortcut.TargetPath -ne (Join-Path $app 'Sims4ModSieve.exe')) { throw "Invalid $folder shortcut." }
    }
    Write-Host 'PASS PayloadRegistrationAndShortcuts'
    $unknown = Join-Path $app 'user-note.txt'
    Set-Content -LiteralPath $unknown -Value 'keep user file'
    $settingsBefore = Get-Content -LiteralPath (Join-Path $data 'duplicate-settings.json') -Raw
    Set-Content -LiteralPath (Join-Path $app 'README.md') -Value 'previous version'
    Run-Setup 'CoverInstall'
    if ((Get-FileHash -LiteralPath (Join-Path $app 'README.md')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $source 'README.md')).Hash) { throw 'Upgrade did not replace program content.' }
    if ((Get-Content -LiteralPath (Join-Path $data 'duplicate-settings.json') -Raw) -ne $settingsBefore) { throw 'Upgrade changed settings.' }
    $lock = [IO.File]::Open((Join-Path $data 'application.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Run-Setup 'RunningAppBlocksManualInstall' $app $false
        Run-Uninstall 'RunningAppBlocksUninstall' $false
        $handoff = Join-Path $case 'handoff'
        $update = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
            ('/DIR="' + $app + '"'), ('/SIEVEHANDOFF="' + $handoff + '"'), ('/LOG="' + (Join-Path $case 'handoff.log') + '"')) -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $handoff)) {
            if ($update.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Installer did not acknowledge handoff.' }
            Start-Sleep -Milliseconds 100
        }
        if ($update.HasExited) { throw 'Installer did not wait for running app.' }
        [IO.File]::WriteAllText($handoff + '.accepted', $version, [Text.UTF8Encoding]::new($false))
    } finally { $lock.Dispose() }
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
    Write-Host "Installer test evidence: $case"
} finally {
    $diagnostics = Join-Path $root 'artifacts/ci/installer'
    New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
    Get-ChildItem -LiteralPath $case -Filter '*.log' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $diagnostics -Force }
    # 仅清理这个隔离 AppId；失败时保留文件与日志供排查。
    if ((Test-Path -LiteralPath $registration) -and (Test-Path -LiteralPath (Join-Path $app 'unins000.exe'))) {
        Set-Content -LiteralPath (Join-Path $data 'disabled-mods.json') -Value '[]'
        Run-Uninstall 'CleanupTestInstallation'
    }
}
