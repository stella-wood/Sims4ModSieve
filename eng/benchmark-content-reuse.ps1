# 在仓库根运行；依赖需已 restore。扫描只读，输出写入 artifacts/perf 的新目录。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$Picker = New-Object System.Windows.Forms.FolderBrowserDialog
$Picker.Description = '选择用于 Off/On 对比的 Mods 文件夹'
try {
    if ($Picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw '未选择来源' }
    $Source = $Picker.SelectedPath
} finally {
    $Picker.Dispose()
}
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Dotnet = Join-Path $Repo 'eng\dotnet.cmd'
$Project = Join-Path $Repo 'eng\Sims4ModSieve.Benchmarks'
$Binary = Join-Path $Project 'bin\Release\net10.0\Sims4ModSieve.Benchmarks.dll'
$OutputRoot = Join-Path $Repo ('artifacts\perf\phase3-' + (Get-Date -Format yyyyMMdd-HHmmss-fff))
$Revision = (git -C $Repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法取得 Git 版本' }
if (@(git -C $Repo status --porcelain).Count -gt 0) { $Revision += '+dirty' }
& $Dotnet build $Project -c Release --no-restore --nologo -m:1
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
$Baseline = $null
$Rows = @()
$Round = 0
foreach ($Mode in @('off', 'on', 'off', 'on', 'off', 'on')) {
    $Round++
    $Output = Join-Path $OutputRoot "$Mode-r$Round"
    $Arguments = @($Binary, 'run', '--scope', 'all', '--source', $Source, '--mods-root', $Source,
        '--output', $Output, '--sample-id', 'phase3-user', '--runs', '1',
        '--hash-concurrency', '2', '--index-concurrency', '8', '--content-concurrency', '8',
        '--content-reuse', $Mode, '--revision', $Revision, '--storage', 'manual experiment; medium not asserted')
    if ($Baseline) { $Arguments += @('--baseline', $Baseline) }
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "基准或一致性校验失败：$Mode 第 $Round 轮；请查看 $Output" }
    $Summary = Get-Content -LiteralPath (Join-Path $Output 'summary.json') -Raw | ConvertFrom-Json
    if (!$Summary.valid -or $Summary.completedRuns -ne 1) { throw "该轮没有完整结果：$Output" }
    if (!$Baseline) { $Baseline = $Output }
    $Run = @($Summary.runMetrics)[0]
    $Stage = @($Run.stages | Where-Object { $null -ne $_.contentReuse })[0]
    if ($null -eq $Stage) { throw '缺少资源内容指标' }
    $Stats = $Stage.contentReuse
    $Rows += [pscustomobject]@{
        Mode = $Mode; Round = $Round; TotalMs = [double]$Run.milliseconds
        ContentMs = [double]$Stage.milliseconds; Decompressions = [long]$Stage.decompressionAttempts
        Reused = [long]$Stats.savedDecompressions; AvoidedOutputBytes = [long]$Stats.savedOutputBytes
        ExtraReadGrossBytes = [long]$Stats.grossAdditionalReadBytes
        ExtraReadNetBytes = [long]$Stats.netAdditionalReadBytes
        PeakWorkingSetMiB = [double]$Run.processLifetimePeakWorkingSetBytes / 1MB
    }
}
function Get-Median($Values) {
    $Sorted = @($Values | Sort-Object)
    $Middle = [int][Math]::Floor($Sorted.Count / 2)
    if ($Sorted.Count % 2) { return [double]$Sorted[$Middle] }
    return ([double]$Sorted[$Middle - 1] + [double]$Sorted[$Middle]) / 2
}
$Rows | Export-Csv -LiteralPath (Join-Path $OutputRoot 'rounds.csv') -NoTypeInformation -Encoding UTF8
$Table = foreach ($Mode in @('off', 'on')) {
    $Group = @($Rows | Where-Object { $_.Mode -eq $Mode })
    [pscustomobject]@{
        Mode = $Mode; Runs = $Group.Count
        TotalMs = [Math]::Round((Get-Median $Group.TotalMs), 2)
        ContentMs = [Math]::Round((Get-Median $Group.ContentMs), 2)
        Decompressions = Get-Median $Group.Decompressions
        Reused = Get-Median $Group.Reused
        AvoidedOutputMiB = [Math]::Round((Get-Median $Group.AvoidedOutputBytes) / 1MB, 2)
        ExtraReadGrossMiB = [Math]::Round((Get-Median $Group.ExtraReadGrossBytes) / 1MB, 2)
        ExtraReadNetMiB = [Math]::Round((Get-Median $Group.ExtraReadNetBytes) / 1MB, 2)
        PeakWorkingSetMiB = [Math]::Round((Get-Median $Group.PeakWorkingSetMiB), 2)
    }
}
$Table | Export-Csv -LiteralPath (Join-Path $OutputRoot 'medians.csv') -NoTypeInformation -Encoding UTF8
$Table | Format-Table -AutoSize | Out-String -Width 240 | Write-Host
Write-Host "六轮业务快照一致。结果：$OutputRoot"
