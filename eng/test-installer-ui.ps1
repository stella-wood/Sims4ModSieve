[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$allowed = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/installer-tests')) + [IO.Path]::DirectorySeparatorChar
$case = [IO.Path]::GetFullPath($TestDirectory)
if (-not $case.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a test-installer.ps1 fixture.' }
$setup = @(Get-ChildItem -LiteralPath $case -Filter '*-setup.exe' -File)
if ($setup.Count -ne 1 -or $setup[0].VersionInfo.ProductName -notlike '*安装器测试*') { throw 'Not an isolated test installer.' }
$app = Join-Path $case 'App 测试目录'
$data = Join-Path $case 'Sims4ModSieve'
$screenshots = Join-Path $root 'artifacts/installer-review'
New-Item -ItemType Directory -Path $screenshots -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerReviewWindows {
    public delegate bool Visitor(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(Visitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Visitor visitor, IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint="SendMessageW", CharSet=CharSet.Unicode)] static extern IntPtr ReadText(IntPtr window, uint message, IntPtr index, StringBuilder text);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
    public static void Activate(IntPtr window) {
        ShowWindow(window, 9);
        if (!SetForegroundWindow(window)) {
            keybd_event(0x12,0,0,IntPtr.Zero);
            SetForegroundWindow(window);
            keybd_event(0x12,0,2,IntPtr.Zero);
        }
    }
    public static bool Click(IntPtr button) {
        Activate(GetAncestor(button,2));
        var type = new StringBuilder(256); GetClassName(button,type,type.Capacity);
        if (type.ToString().EndsWith("BitmapButton")) {
            if (!GetClientRect(button,out var bounds)) return false;
            var point = (IntPtr)((bounds.Right/2) | ((bounds.Bottom/2)<<16));
            return PostMessage(button,0x0201,(IntPtr)1,point) && PostMessage(button,0x0202,IntPtr.Zero,point);
        }
        return PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);
    }
    public static string Text(IntPtr window) { var text = new StringBuilder(1024); GetWindowText(window,text,text.Capacity); return text.ToString(); }
    public static int[] ProcessIds = Array.Empty<int>();
    public static IntPtr[] Windows() {
        var result = new List<IntPtr>();
        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var process); if (IsWindowVisible(window) && IsWindowEnabled(window) && ProcessIds.Contains((int)process)) result.Add(window); return true; }, IntPtr.Zero);
        return result.ToArray();
    }
    public static string WindowText(IntPtr window) {
        var result = new StringBuilder(Text(window));
        EnumChildWindows(window, (child, _) => { if (IsWindowVisible(child)) result.AppendLine(Text(child)); return true; }, IntPtr.Zero);
        return result.ToString();
    }
    public static IntPtr Button(IntPtr window, string label) {
        var result = IntPtr.Zero;
        var labels = label switch {
            "重试" => new[] { "重试", "Retry" }, "取消" => new[] { "取消", "Cancel" },
            "是" => new[] { "是", "Yes" }, "否" => new[] { "否", "No" }, "确定" => new[] { "确定", "OK" }, _ => new[] { label }
        };
        EnumChildWindows(window, (child, _) => {
            var type = new StringBuilder(256); GetClassName(child,type,type.Capacity);
            var caption = Text(child).Replace("&", "");
            if (type.ToString().EndsWith("Button",StringComparison.OrdinalIgnoreCase) && IsWindowVisible(child) && IsWindowEnabled(child)
                && labels.Any(value => caption.StartsWith(value,StringComparison.OrdinalIgnoreCase))) { result = child; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static IntPtr DataCheckbox(IntPtr window) {
        var result = IntPtr.Zero;
        EnumChildWindows(window, (child, _) => { if (IsWindowVisible(child) && Text(child).StartsWith("同时删除本工具的设置")) { result=child; return false; } return true; }, IntPtr.Zero);
        return result;
    }
    public static string[] FolderItems(IntPtr window) {
        var list = IntPtr.Zero;
        EnumChildWindows(window,(child,_)=> { var type=new StringBuilder(256); GetClassName(child,type,type.Capacity); if (type.ToString().EndsWith("ListBox")) { list=child; return false; } return true; },IntPtr.Zero);
        if(list==IntPtr.Zero) throw new Exception("Disabled folder list is missing");
        var result=new List<string>();
        var count=SendMessage(list,0x018B,IntPtr.Zero,IntPtr.Zero).ToInt32();
        for(var i=0;i<count;i++){ var length=SendMessage(list,0x018A,(IntPtr)i,IntPtr.Zero).ToInt32(); var text=new StringBuilder(length+1); ReadText(list,0x0189,(IntPtr)i,text); result.Add(text.ToString()); }
        return result.ToArray();
    }
}
'@

function Update-ReviewProcesses {
    $processes = @(Get-CimInstance Win32_Process | Where-Object Name -ne 'explorer.exe' | Select-Object ProcessId,ParentProcessId)
    $ids = @($script:reviewProcess.Id)
    for ($level = 0; $level -lt 6; $level++) { $ids += @($processes | Where-Object { $_.ParentProcessId -in $ids } | ForEach-Object { [int]$_.ProcessId }) }
    [InstallerReviewWindows]::ProcessIds = [int[]]($ids | Select-Object -Unique)
    return @($processes | Where-Object { $_.ProcessId -in $ids }).Count -gt 0
}

function Wait-ReviewExit {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Update-ReviewProcesses) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Installer process did not exit.' }
        Start-Sleep -Milliseconds 100
    }
}

function Assert-NoRuntimeError {
    foreach ($window in [InstallerReviewWindows]::Windows()) {
        $text = [InstallerReviewWindows]::WindowText($window)
        if ($text -match 'Runtime error|Cannot call .* function|Internal error|内部错误') { throw "Installer runtime error: $text" }
    }
}

function Find-Button([string]$Label, [int]$Seconds = 20, [switch]$AllowExit) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $running = Update-ReviewProcesses
        if ($AllowExit -and -not $running) { return $null }
        Assert-NoRuntimeError
        foreach ($window in [InstallerReviewWindows]::Windows()) {
            $button = [InstallerReviewWindows]::Button($window, $Label)
            if ($button -ne [IntPtr]::Zero) { return @{ Window = $window; Button = $button } }
        }
        Start-Sleep -Milliseconds 100
    }
    $visible = @([InstallerReviewWindows]::Windows() | ForEach-Object { [InstallerReviewWindows]::WindowText($_) }) -join "`n"
    throw "No Chinese installer button: $Label. Visible windows: $visible"
}

function Save-Window([IntPtr]$Window, [string]$Name) {
    [InstallerReviewWindows]::Activate($Window)
    Start-Sleep -Milliseconds 300
    $bounds = [InstallerReviewWindows+Bounds]::new()
    if (-not [InstallerReviewWindows]::GetWindowRect($Window, [ref]$bounds)) { throw 'Cannot read installer window.' }
    $bitmap = [Drawing.Bitmap]::new($bounds.Right - $bounds.Left, $bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $screenshots $Name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Click-Button($Target) {
    if (-not [InstallerReviewWindows]::Click($Target.Button)) { throw 'Cannot click installer button.' }
}

$wizard = Start-Process -FilePath $setup[0].FullName -ArgumentList @('/SP-', ('/DIR="' + $app + '"')) -PassThru
$script:reviewProcess = $wizard
$cancel = Find-Button '取消'
Save-Window $cancel.Window 'installer-chinese-wizard.png'
Click-Button $cancel
Start-Sleep -Milliseconds 300
$confirmation = Find-Button '是' -AllowExit
if ($confirmation) { Click-Button $confirmation }
Wait-ReviewExit
Write-Host 'PASS ChineseWizardOpensAndCancels'

function Install-TestFixture {
    $install = Start-Process -FilePath $setup[0].FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $app + '"'), ('/LOG="' + (Join-Path $case 'native-ui-install.log') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Cannot reinstall isolated fixture: $($install.ExitCode). See $case/native-ui-install.log" }
}

function Find-RunningAppPrompt([switch]$AdvanceWizard) {
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Update-ReviewProcesses)) { throw 'Installer exited before showing the running-app prompt.' }
        Assert-NoRuntimeError
        foreach ($window in [InstallerReviewWindows]::Windows()) {
            $retry = [InstallerReviewWindows]::Button($window, '重试')
            if ($retry -ne [IntPtr]::Zero) {
                if ([InstallerReviewWindows]::WindowText($window) -notlike '*请先正常关闭*') { throw 'Unexpected retry prompt.' }
                return @{ Window = $window; Button = $retry }
            }
        }
        if ($AdvanceWizard) {
            foreach ($window in [InstallerReviewWindows]::Windows()) {
                foreach ($label in @('下一步', '安装', '我同意此协议')) {
                    $button = [InstallerReviewWindows]::Button($window, $label)
                    if ($button -ne [IntPtr]::Zero) { Click-Button @{Button=$button}; break }
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Running-app prompt was not shown.'
}

Install-TestFixture
$lock = [IO.File]::Open((Join-Path $data 'application.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $manual = Start-Process -FilePath $setup[0].FullName -ArgumentList @('/SP-', ('/DIR="' + $app + '"'),
        ('/LOG="' + (Join-Path $case 'manual-running-install.log') + '"')) -PassThru
    $script:reviewProcess = $manual
    $retry = Find-RunningAppPrompt -AdvanceWizard
    Save-Window $retry.Window 'installer-running-app-prompt.png'
    $lock.Dispose()
    Click-Button $retry
    Click-Button (Find-Button '完成')
    Wait-ReviewExit
    if ($manual.ExitCode -ne 0) { throw 'Manual installation did not resume after lock release.' }
} finally { $lock.Dispose() }
Write-Host 'PASS ManualRunningInstallPromptsAndRetries'

$lock = [IO.File]::Open((Join-Path $data 'application.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $manualUninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/SILENT', '/SIEVEINTERACTIVE=1', '/NORESTART',
        ('/LOG="' + (Join-Path $case 'manual-running-uninstall.log') + '"')) -PassThru
    $script:reviewProcess = $manualUninstall
    $retry = Find-RunningAppPrompt
    Save-Window $retry.Window 'uninstaller-running-app-prompt.png'
    Click-Button (Find-Button '取消')
    Wait-ReviewExit
    if ($manualUninstall.ExitCode -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe'))) { throw 'Cancelled uninstall removed program files.' }
} finally { $lock.Dispose() }
Write-Host 'PASS ManualRunningUninstallPromptsAndCancels'

function Select-DataCleanup([IntPtr]$Window, [bool]$Selected) {
    $checkbox = [InstallerReviewWindows]::DataCheckbox($Window)
    if ($checkbox -eq [IntPtr]::Zero) { throw 'Uninstall data checkbox is missing.' }
    $checked = [InstallerReviewWindows]::SendMessage($checkbox, 0x00F0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -eq 1
    if ($checked -ne $Selected) { Click-Button @{ Button=$checkbox } }
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while (([InstallerReviewWindows]::SendMessage($checkbox, 0x00F0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -eq 1) -ne $Selected) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Uninstall data checkbox did not change.' }
        Start-Sleep -Milliseconds 50
    }
}

function Start-CustomUninstall([bool]$ClearData = $false) {
    $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C897868A-0997-463F-B377-A68A616F831E}-InstallerTests_is1'
    $command = (Get-ItemProperty -LiteralPath $registration).UninstallString
    if ($command -notmatch '^"([^"]+)" (.+)$' -or $Matches[1] -ne (Join-Path $app 'unins000.exe')) { throw 'Unexpected registered uninstall command.' }
    $process = Start-Process -FilePath $Matches[1] -ArgumentList @($Matches[2], '/NORESTART') -PassThru
    $script:reviewProcess = $process
    $question = Find-Button '是'
    $text = [InstallerReviewWindows]::WindowText($question.Window)
    if ($text -notlike '*你的 Mods、存档和游戏文件不会被删除*') { throw 'Game file reassurance is missing.' }
    $checkbox = [InstallerReviewWindows]::DataCheckbox($question.Window)
    if ($checkbox -eq [IntPtr]::Zero -or [InstallerReviewWindows]::SendMessage($checkbox, 0x00F0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -ne 0) { throw 'Data cleanup must default to unchecked.' }
    Select-DataCleanup $question.Window $ClearData
    return $process
}

function Finish-Uninstall($Process, [switch]$AllowPartialCleanup) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Update-ReviewProcesses) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Interactive uninstall timed out.' }
        Assert-NoRuntimeError
        foreach ($window in [InstallerReviewWindows]::Windows()) {
            if ([InstallerReviewWindows]::Button($window, '是') -ne [IntPtr]::Zero -and [InstallerReviewWindows]::DataCheckbox($window) -eq [IntPtr]::Zero) { throw 'A second Yes/No confirmation appeared.' }
            $button = [InstallerReviewWindows]::Button($window, '确定')
            if ($button -ne [IntPtr]::Zero) {
                if (-not $AllowPartialCleanup -or [InstallerReviewWindows]::WindowText($window) -notlike '*部分本机数据未能删除*') { throw 'Unexpected uninstall message.' }
                Click-Button @{Button=$button}
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($Process.ExitCode -ne 0) { throw "Interactive uninstall failed: $($Process.ExitCode)" }
}

Install-TestFixture
Set-Content -LiteralPath (Join-Path $data 'test-user-data.txt') -Value 'temporary data'
$uninstall = Start-CustomUninstall
$question = Find-Button '是'
Save-Window $question.Window 'uninstall-confirm.png'
Select-DataCleanup $question.Window $true
Save-Window $question.Window 'uninstall-options.png'
Click-Button (Find-Button '否')
Wait-ReviewExit
if (-not (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe')) -or -not (Test-Path -LiteralPath (Join-Path $data 'test-user-data.txt'))) { throw 'Cancelled options deleted files.' }
Write-Host 'PASS SingleUninstallConfirmationDefaultsToKeepDataAndCanCancel'

$uninstall = Start-CustomUninstall
$question = Find-Button '是'
[InstallerReviewWindows]::PostMessage($question.Window, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
[InstallerReviewWindows]::PostMessage($question.Window, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
Finish-Uninstall $uninstall
if (-not (Test-Path -LiteralPath (Join-Path $data 'test-user-data.txt'))) { throw 'Default uninstall deleted user data.' }
Write-Host 'PASS DefaultUninstallPreservesData'

Install-TestFixture
$uninstall = Start-CustomUninstall $true
Click-Button (Find-Button '是')
Finish-Uninstall $uninstall
if (Test-Path -LiteralPath $data) { throw 'Confirmed data cleanup did not remove temporary user data.' }
Write-Host 'PASS ExplicitDataCleanup'

Install-TestFixture
$outside = Join-Path $case 'Outside'
New-Item -ItemType Directory -Path $outside -Force | Out-Null
$sentinel = Join-Path $outside 'keep.txt'
Set-Content -LiteralPath $sentinel -Value 'do not follow junction'
$link = Join-Path $data 'linked-data'
New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
$uninstall = Start-CustomUninstall $true
Click-Button (Find-Button '是')
Finish-Uninstall $uninstall -AllowPartialCleanup
if ((Get-Content -LiteralPath $sentinel).Trim() -ne 'do not follow junction') { throw 'Data cleanup followed directory link.' }
if (-not (Test-Path -LiteralPath $link)) { throw 'Directory link should remain for manual cleanup.' }
[IO.Directory]::Delete($link)
Write-Host 'PASS DataCleanupDoesNotFollowJunction'

Install-TestFixture
$firstFolder = Join-Path $case '临时 Mods/作者甲'
$secondFolder = Join-Path $case '临时 Mods/作者乙'
New-Item -ItemType Directory -Path $firstFolder,$secondFolder -Force | Out-Null
$records = @()
foreach ($entry in @(@{Folder=$firstFolder;Name='a'}, @{Folder=$firstFolder;Name='b'}, @{Folder=$secondFolder;Name='c'})) {
    $original = Join-Path $entry.Folder ($entry.Name + '.package')
    Set-Content -LiteralPath ($original + '.off') -Value 'temporary disabled mod'
    $records += @{ Id=[Guid]::NewGuid().ToString('N'); OriginalPath=$original; Size=24; LastWriteTimeUtc=[DateTime]::UtcNow; DisabledAtUtc=[DateTime]::UtcNow }
}
$ledger = Join-Path $data 'disabled-mods.json'
$ledgerJson = ConvertTo-Json -InputObject $records -Depth 10
Set-Content -LiteralPath $ledger -Value $ledgerJson
Set-Content -LiteralPath (Join-Path $data 'test-user-data.txt') -Value 'temporary data'
$ledgerBefore = Get-Content -LiteralPath $ledger -Raw
$uninstall = Start-CustomUninstall $true
Click-Button (Find-Button '是')
$danger = Find-Button '仍然卸载'
$warningText = [InstallerReviewWindows]::WindowText($danger.Window)
if ($warningText -notlike '*还有 3 个 Mod*' -or $warningText -notlike '*手动去掉 .off 后缀恢复*') { throw 'Disabled Mod warning text or count is wrong.' }
$folders = [InstallerReviewWindows]::FolderItems($danger.Window)
if ($folders.Count -ne 2 -or (Compare-Object @($firstFolder,$secondFolder) $folders)) { throw 'Disabled folder paths differ.' }
Save-Window $danger.Window 'uninstall-disabled-warning.png'
$selectedFolder = $folders[0]
Click-Button (Find-Button '打开文件夹')
$shellApplication = New-Object -ComObject Shell.Application
$deadline = [DateTime]::UtcNow.AddSeconds(15)
$folderOpened = $false
while (-not $folderOpened -and [DateTime]::UtcNow -lt $deadline) {
    foreach ($shellWindow in @($shellApplication.Windows())) {
        try { if ($shellWindow.Document.Folder.Self.Path -eq $selectedFolder) { $folderOpened = $true } } catch { }
    }
    if (-not $folderOpened) { Start-Sleep -Milliseconds 100 }
}
if (-not $folderOpened) { throw 'Open folder did not show the selected disabled folder.' }
[InstallerReviewWindows]::Activate($danger.Window)
Start-Sleep -Milliseconds 200
[InstallerReviewWindows]::PostMessage($danger.Window, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
[InstallerReviewWindows]::PostMessage($danger.Window, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
Wait-ReviewExit
if (-not (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe')) -or (Get-Content -LiteralPath $ledger -Raw) -ne $ledgerBefore) { throw 'Default warning action did not cancel uninstall.' }
Write-Host 'PASS DisabledWarningListsFoldersOpensFolderAndDefaultsToCancel'

$uninstall = Start-CustomUninstall $true
Click-Button (Find-Button '是')
Click-Button (Find-Button '仍然卸载')
Finish-Uninstall $uninstall
if ((Get-Content -LiteralPath $ledger -Raw) -ne $ledgerBefore) { throw 'Continuing uninstall changed the disabled ledger.' }
if (@(Get-ChildItem -LiteralPath $data -Force).Count -ne 1 -or (Test-Path -LiteralPath (Join-Path $data 'test-user-data.txt'))) { throw 'Data cleanup did not retain only disabled-mods.json.' }
foreach ($record in $records) { if (-not (Test-Path -LiteralPath ($record.OriginalPath + '.off'))) { throw 'Uninstall changed a disabled Mod.' } }
Write-Host 'PASS ContinueDisabledUninstallCleansDataButPreservesLedgerAndMods'

Install-TestFixture
if ((Get-Content -LiteralPath $ledger -Raw) -ne $ledgerBefore) { throw 'Reinstall lost disabled records.' }
Set-Content -LiteralPath (Join-Path $data 'test-user-data.txt') -Value 'keep by default'
$uninstall = Start-CustomUninstall
Click-Button (Find-Button '是')
Click-Button (Find-Button '仍然卸载')
Finish-Uninstall $uninstall
if (-not (Test-Path -LiteralPath (Join-Path $data 'test-user-data.txt'))) { throw 'Continuing uninstall without cleanup deleted data.' }
Write-Host 'PASS ReinstallFindsDisabledLedgerAndDefaultUninstallRetainsAllData'

Install-TestFixture
Set-Content -LiteralPath $ledger -Value '{'
$uninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/SILENT', '/SIEVEINTERACTIVE=1', '/NORESTART') -PassThru
$script:reviewProcess = $uninstall
$errorMessage = Find-Button '确定'
if ([InstallerReviewWindows]::WindowText($errorMessage.Window) -notlike '*无法读取禁用记录*' -or [InstallerReviewWindows]::Button($errorMessage.Window, '仍然卸载') -ne [IntPtr]::Zero) { throw 'Unreadable ledger allowed unsafe continuation.' }
Click-Button $errorMessage
Wait-ReviewExit
if (-not (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe'))) { throw 'Unreadable ledger did not stop uninstall.' }
Write-Host 'PASS UnreadableLedgerStopsInteractiveUninstallWithoutContinuation'
Set-Content -LiteralPath $ledger -Value '[]'
$cleanup = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($cleanup.ExitCode -ne 0) { throw 'Cannot remove isolated test installation.' }
Write-Host "Installer screenshots: $screenshots"
