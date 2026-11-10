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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
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
}
'@

function Update-ReviewProcesses {
    $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
    $ids = @($script:reviewProcess.Id)
    for ($level = 0; $level -lt 3; $level++) { $ids += @($processes | Where-Object { $_.ParentProcessId -in $ids } | ForEach-Object { [int]$_.ProcessId }) }
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
    throw "No Chinese installer button: $Label"
}

function Save-Window([IntPtr]$Window, [string]$Name) {
    [InstallerReviewWindows]::SetForegroundWindow($Window) | Out-Null
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
    if (-not [InstallerReviewWindows]::PostMessage($Target.Button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) { throw 'Cannot click installer button.' }
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
    $manualUninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/NORESTART',
        ('/LOG="' + (Join-Path $case 'manual-running-uninstall.log') + '"')) -PassThru
    $script:reviewProcess = $manualUninstall
    $retry = Find-RunningAppPrompt
    Save-Window $retry.Window 'uninstaller-running-app-prompt.png'
    Click-Button (Find-Button '取消')
    Wait-ReviewExit
    if ($manualUninstall.ExitCode -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $app 'Sims4ModSieve.exe'))) { throw 'Cancelled uninstall removed program files.' }
} finally { $lock.Dispose() }
Write-Host 'PASS ManualRunningUninstallPromptsAndCancels'

function Uninstall-WithDataCleanup([string]$Screenshot) {
    $uninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList '/NORESTART' -PassThru
    $script:reviewProcess = $uninstall
    $question = Find-Button '是'
    Save-Window $question.Window $Screenshot
    Click-Button $question
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Update-ReviewProcesses) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Interactive uninstall timed out.' }
        Assert-NoRuntimeError
        foreach ($window in [InstallerReviewWindows]::Windows()) {
            foreach ($label in @('是', '确定')) {
                $button = [InstallerReviewWindows]::Button($window, $label)
                if ($button -ne [IntPtr]::Zero) { Click-Button @{Button=$button}; Start-Sleep -Milliseconds 150 }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($uninstall.ExitCode -ne 0) { throw 'Interactive uninstall failed.' }
}

Install-TestFixture
Set-Content -LiteralPath (Join-Path $data 'test-user-data.txt') -Value 'temporary data'
Uninstall-WithDataCleanup 'installer-remove-data-question.png'
if (Test-Path -LiteralPath $data) { throw 'Confirmed data cleanup did not remove temporary user data.' }
Write-Host 'PASS ExplicitDataCleanup'

Install-TestFixture
$outside = Join-Path $case 'Outside'
New-Item -ItemType Directory -Path $outside -Force | Out-Null
$sentinel = Join-Path $outside 'keep.txt'
Set-Content -LiteralPath $sentinel -Value 'do not follow junction'
$link = Join-Path $data 'linked-data'
New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
Uninstall-WithDataCleanup 'installer-remove-data-junction.png'
if ((Get-Content -LiteralPath $sentinel).Trim() -ne 'do not follow junction') { throw 'Data cleanup followed directory link.' }
if (-not (Test-Path -LiteralPath $link)) { throw 'Directory link should remain for manual cleanup.' }
[IO.Directory]::Delete($link)
Write-Host 'PASS DataCleanupDoesNotFollowJunction'
Write-Host "Installer screenshots: $screenshots"
