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
$OnDiagnostics = @()
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
    if ($Mode -eq 'on') {
        if ($Stats.bypassReason) { throw "On 被旁路：$($Stats.bypassReason)" }
        if ($Stats.digestPotentialSavings -ne $Stats.accountedSavings) { throw '复用损失计数不闭合' }
        $OnDiagnostics += $Stats
    }
    $Rows += [pscustomobject]@{
        Mode = $Mode; Round = $Round; TotalMs = [double]$Run.milliseconds
        ContentMs = [double]$Stage.milliseconds; Decompressions = [long]$Stage.decompressionAttempts
        ConfirmMs = [double]$Stats.confirmationMilliseconds
        ConfirmOpens = [long]$Stats.confirmationHandleOpens; ConfirmStamps = [long]$Stats.confirmationStampChecks
        HandleEvictions = [long]$Stats.confirmationHandleEvictions
        DigestPotential = [long]$Stats.digestPotentialSavings
        ByteLimitLoss = [long]$Stats.confirmationByteLimitLoss; CollisionLimitLoss = [long]$Stats.collisionSetLimitLoss
        DisqualifiedLoss = [long]$Stats.disqualifiedBeforeConfirmationLoss
        ReadFailureLoss = [long]$Stats.confirmationReadFailureLoss; ByteMismatchLoss = [long]$Stats.byteMismatchLoss
        PostConfirmationLoss = [long]$Stats.postConfirmationLoss
        RepeatedOriginalTargets = [long]$Stats.repeatedOriginalTargets
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
# 额外统计放在六轮计时之外，用同一份来源核对预测口径和损失。
$StatsOutput = Join-Path $OutputRoot 'raw-reuse-stats'
& $Dotnet $Binary 'raw-reuse-stats' '--source' $Source '--output' $StatsOutput
if ($LASTEXITCODE -ne 0) { throw "只读预测统计失败：$StatsOutput" }
$Prediction = Get-Content -LiteralPath (Join-Path $StatsOutput 'stats.json') -Raw | ConvertFrom-Json
$Accounting = foreach ($Stats in $OnDiagnostics) {
    $ScopeMatches = $Prediction.candidateResources -eq $Stats.targetResources -and `
        $Prediction.needsDecompressionResources -eq $Stats.needsDecompressionResources -and `
        $Prediction.storedBytes -eq $Stats.storedBytes -and `
        $Prediction.metadataGroups -eq $Stats.metadataGroups -and `
        $Prediction.singletonMetadataResources -eq $Stats.singletonMetadataResources -and `
        $Prediction.digestPotentialSavings -eq $Stats.digestPotentialSavings
    $DigestDelta = [long]$Prediction.digestPotentialSavings - [long]$Stats.digestPotentialSavings
    $Gap = [long]$Prediction.savedDecompressions - [long]$Stats.savedDecompressions
    $Loss = [long]$Stats.accountedSavings - [long]$Stats.savedDecompressions
    $Excluded = [long]$Prediction.invalidContentSavingsExcluded
    if ($Gap -ne ($DigestDelta + $Loss - $Excluded)) { throw '预测差额计数不闭合' }
    [pscustomobject]@{
        ScopeMatches = $ScopeMatches
        Predicted = [long]$Prediction.savedDecompressions; Saved = [long]$Stats.savedDecompressions; Gap = $Gap
        DigestPotentialDelta = $DigestDelta; InvalidContentAlreadyExcludedByStats = $Excluded
        DisqualifiedBeforeConfirmation = [long]$Stats.disqualifiedBeforeConfirmationLoss
        ConfirmationByteLimit = [long]$Stats.confirmationByteLimitLoss
        CollisionSetLimit = [long]$Stats.collisionSetLimitLoss; ByteMismatch = [long]$Stats.byteMismatchLoss
        ConfirmationReadFailure = [long]$Stats.confirmationReadFailureLoss
        ConfirmationRejected = [long]$Stats.confirmationRejectedLoss
        ReaderFallback = [long]$Stats.readerFallbackLoss; PublicationVersion = [long]$Stats.publicationVersionLoss
        RepeatedOriginalTargets = [long]$Stats.repeatedOriginalTargets
    }
}
$Accounting | Export-Csv -LiteralPath (Join-Path $OutputRoot 'reuse-accounting.csv') -NoTypeInformation -Encoding UTF8
$Accounting | Format-Table -AutoSize | Out-String -Width 260 | Write-Host
if (@($Accounting | Where-Object { !$_.ScopeMatches }).Count -gt 0) {
    Write-Warning '预测和 On 的候选/元数据/摘要口径未对齐，查看 JSON；差额不能全归因于确认上限。'
}
Write-Host "六轮业务快照一致。结果和复用差额对账：$OutputRoot"
