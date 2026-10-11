using System.Windows;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Infrastructure;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ConflictResultSession conflictSession = new();
    private readonly IPackageConflictScanner packageConflictScanner;
    private readonly IResourceContentComparer resourceContentComparer;
    private readonly IScriptModuleScanner scriptModuleScanner;
    private CancellationTokenSource? conflictCancellation;
    private int conflictGeneration;
    private long conflictRowsRequest;
    private CancellationTokenSource? conflictRowsCancellation;
    private IReadOnlyList<ConflictResultRow> conflictRows = [];
    private int visibleConflictParentCount;
    public Task ConflictRowsRefreshTask { get; private set; } = Task.CompletedTask;
    private string conflictSection = "overview";
    private string conflictView = "resources";
    private string conflictFilter = "all";
    private string conflictStatus = "尚未扫描";
    private string conflictPhase = string.Empty;
    private string conflictRunState = "initial";
    private int conflictCompleted;
    // 进度条上限：总数未知或为零时固定为 1，不代表真实总数。
    private int conflictProgressMaximum = 1;
    private int? conflictProgressTotal;
    private bool conflictIndeterminate = true;

    public RelayCommand OpenConflictSectionCommand { get; private set; } = null!;
    public RelayCommand ConflictBackCommand { get; private set; } = null!;
    public RelayCommand SelectConflictViewCommand { get; private set; } = null!;
    public RelayCommand SelectConflictFilterCommand { get; private set; } = null!;
    public AsyncRelayCommand StartConflictScanCommand { get; private set; } = null!;
    public RelayCommand ToggleConflictExpandedCommand { get; private set; } = null!;
    public RelayCommand CancelConflictScanCommand { get; private set; } = null!;

    public string ConflictSection => conflictSection;
    public bool IsConflictOverview => IsConflictVisible && conflictSection == "overview";
    public bool IsConflictBasic => IsConflictVisible && conflictSection == "basic";
    public bool IsConflictUnavailable => IsConflictVisible && conflictSection is "logs" or "health" or "bisect";
    public string ConflictUnavailableTitle => conflictSection switch
    {
        "logs" => "报错日志分析",
        "health" => "健康性检查",
        _ => "二分排查",
    };
    public string ConflictView => conflictView;
    public string ConflictFilter => conflictFilter;
    public bool IsResourceViewSelected => conflictView == "resources";
    public bool IsScriptViewSelected => conflictView == "scripts";
    public bool IsProblemViewSelected => conflictView == "problems";
    public bool IsAllFilterSelected => conflictFilter == "all";
    public bool IsDifferentFilterSelected => conflictFilter == "different";
    public bool IsSameFilterSelected => conflictFilter == "same";
    public string ConflictStatus => conflictStatus;
    public string ConflictPhase => conflictPhase;
    public string ConflictProgressText => conflictProgressTotal is { } total && total > 0 ? $"{conflictCompleted} / {total}" : string.Empty;
    public string ConflictRunState => conflictRunState;
    public bool IsConflictScanning => conflictRunState == "scanning";
    public bool IsConflictNotScanning => !IsConflictScanning;
    public bool ShowConflictFilters => IsConflictBasic && conflictView != "problems";
    public bool IsConflictComplete => conflictRunState == "complete";
    public bool ShowConflictPlaceholder => IsConflictBasic && !IsConflictComplete;
    public bool HasConflictProblems => conflictSession.Problems.Count > 0 || conflictSession.RangeProblems.Count > 0;
    public bool ShowConflictWarning => IsConflictComplete && !HasConflictMatches && !HasConflictProblems;
    public bool HasConflictMatches => conflictSession.Resources.Count > 0 || conflictSession.Scripts.Count > 0;
    public IReadOnlyList<PackageConflictPairSummary> ConflictResources => conflictSession.Resources;
    public IReadOnlyList<ScriptConflictPairSummary> ConflictScripts => conflictSession.Scripts;
    public IReadOnlyList<ConflictProblemRow> ConflictProblems => conflictSession.Problems;
    public IReadOnlyList<ConflictRangeProblem> ConflictRangeProblems => conflictSession.RangeProblems;
    public IReadOnlyList<ConflictResultRow> ConflictRows => conflictRows;
    public int VisibleConflictParentCount => visibleConflictParentCount;
    public bool ShowEmptyConflictRows => IsConflictBasic && IsConflictComplete && visibleConflictParentCount == 0;
    public string EmptyConflictText => conflictView == "problems" ? "没有未完成项" : "没有符合筛选条件的文件对";
    public bool ShowConflictResources => IsConflictBasic && conflictView == "resources";
    public bool ShowConflictScripts => IsConflictBasic && conflictView == "scripts";
    public bool ShowConflictProblems => IsConflictBasic && conflictView == "problems";
    public int ConflictCompleted => conflictCompleted;
    public int ConflictProgressMaximum => conflictProgressMaximum;
    public bool ConflictIndeterminate => conflictIndeterminate;

    private void InitializeConflict()
    {
        OpenConflictSectionCommand = new RelayCommand(section => SelectConflictSection(section as string), _ => !IsBusy);
        ConflictBackCommand = new RelayCommand(_ => SelectConflictSection("overview"), _ => !IsBusy);
        SelectConflictViewCommand = new RelayCommand(view => SelectConflictView(view as string));
        SelectConflictFilterCommand = new RelayCommand(filter => SelectConflictFilter(filter as string));
        ToggleConflictExpandedCommand = new RelayCommand(value =>
        {
            if (value is not ConflictResultRow { IsExpandable: true } row || !IsConflictComplete) return;
            conflictSession.ToggleExpanded(row.Key);
            QueueConflictRowsRefresh();
        });
        StartConflictScanCommand = new AsyncRelayCommand(StartConflictScanAsync,
            exception => ReportConflictFailure(exception), () => !IsBusy && Sources.Any(source => source.IsEnabled));
        CancelConflictScanCommand = new RelayCommand(_ => conflictCancellation?.Cancel(), _ => IsConflictScanning);
    }

    private void ShowConflictOverview()
    {
        RefreshAvailability();
        IsHomeVisible = false;
        IsScannerVisible = false;
        IsConflictVisible = true;
        SelectConflictSection("overview");
    }

    private void SelectConflictSection(string? section)
    {
        if (section is not ("overview" or "basic" or "logs" or "health" or "bisect")) return;
        conflictSection = section;
        OnPropertyChanged(nameof(ConflictSection));
        RefreshConflictVisibility();
    }

    private void RefreshConflictVisibility()
    {
        OnPropertyChanged(nameof(IsConflictOverview));
        OnPropertyChanged(nameof(IsConflictBasic));
        OnPropertyChanged(nameof(IsConflictUnavailable));
        OnPropertyChanged(nameof(ConflictUnavailableTitle));
        OnPropertyChanged(nameof(ShowConflictResources));
        OnPropertyChanged(nameof(ShowConflictScripts));
        OnPropertyChanged(nameof(ShowConflictProblems));
        OnPropertyChanged(nameof(ShowConflictFilters));
        OnPropertyChanged(nameof(IsResourceViewSelected));
        OnPropertyChanged(nameof(IsScriptViewSelected));
        OnPropertyChanged(nameof(IsProblemViewSelected));
        OnPropertyChanged(nameof(ShowEmptyConflictRows));
        OnPropertyChanged(nameof(EmptyConflictText));
        OnPropertyChanged(nameof(ShowConflictPlaceholder));
    }

    private void SelectConflictView(string? view)
    {
        if (view is not ("resources" or "scripts" or "problems")) return;
        conflictView = view;
        if (IsConflictComplete) QueueConflictRowsRefresh();
        OnPropertyChanged(nameof(ConflictView));
        RefreshConflictVisibility();
    }

    private void SelectConflictFilter(string? filter)
    {
        if (filter is not ("all" or "different" or "same")) return;
        conflictFilter = filter;
        OnPropertyChanged(nameof(ConflictFilter));
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsDifferentFilterSelected));
        OnPropertyChanged(nameof(IsSameFilterSelected));
        if (IsConflictComplete) QueueConflictRowsRefresh();
    }

    private void QueueConflictRowsRefresh() => ConflictRowsRefreshTask = RefreshConflictRowsAsync();

    private async Task RefreshConflictRowsAsync()
    {
        var session = conflictSession;
        var request = ++conflictRowsRequest;
        conflictRowsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        conflictRowsCancellation = cancellation;
        var view = conflictView;
        var filter = conflictFilter;
        var expanded = session.GetExpandedKeysSnapshot();
        try
        {
            var projection = await Task.Run(() =>
            {
                var rows = session.BuildVisibleRows(view, filter, expanded, cancellation.Token);
                return (Rows: rows, ParentCount: rows.Count(row => row.Depth == 0));
            }, cancellation.Token);
            if (!ReferenceEquals(session, conflictSession) || request != conflictRowsRequest || !IsConflictComplete) return;
            PublishConflictRows(projection.Rows, projection.ParentCount);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ReferenceEquals(session, conflictSession) || request != conflictRowsRequest) return;
            conflictPhase = "结果显示未完成，请重试";
            OnPropertyChanged(nameof(ConflictPhase));
        }
        finally
        {
            if (ReferenceEquals(conflictRowsCancellation, cancellation)) conflictRowsCancellation = null;
        }
    }

    private void PublishConflictRows(IReadOnlyList<ConflictResultRow> rows, int parentCount)
    {
        conflictRows = rows;
        visibleConflictParentCount = parentCount;
        OnPropertyChanged(nameof(ConflictRows));
        OnPropertyChanged(nameof(VisibleConflictParentCount));
        OnPropertyChanged(nameof(ShowEmptyConflictRows));
        OnPropertyChanged(nameof(EmptyConflictText));
    }

    private void ResetConflictSession()
    {
        conflictGeneration++;
        conflictCancellation?.Cancel();
        conflictRowsRequest++;
        conflictRowsCancellation?.Cancel();
        // 正在投影的旧会话保持只读，清空来源或重扫直接替换身份。
        conflictSession = new();
        PublishConflictRows([], 0);
        conflictView = "resources";
        conflictFilter = "all";
        conflictRunState = "initial";
        conflictStatus = "尚未扫描";
        conflictPhase = string.Empty;
        conflictCompleted = 0;
        conflictProgressMaximum = 1;
        conflictProgressTotal = null;
        conflictIndeterminate = true;
        OnPropertyChanged(nameof(ConflictView));
        OnPropertyChanged(nameof(ConflictFilter));
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsDifferentFilterSelected));
        OnPropertyChanged(nameof(IsSameFilterSelected));
        RefreshConflictVisibility();
        RefreshConflictResults();
    }

    private void RefreshConflictResults()
    {
        foreach (var name in new[] { nameof(ConflictRunState), nameof(ConflictStatus), nameof(ConflictPhase),
            nameof(IsConflictScanning), nameof(IsConflictComplete), nameof(HasConflictProblems),
            nameof(ShowConflictWarning),
            nameof(IsConflictNotScanning),
            nameof(HasConflictMatches), nameof(ConflictResources), nameof(ConflictScripts),
            nameof(ConflictProblems), nameof(ConflictRangeProblems), nameof(ConflictRows),
            nameof(VisibleConflictParentCount), nameof(ConflictCompleted), nameof(ConflictProgressMaximum),
            nameof(ConflictProgressText), nameof(ConflictIndeterminate) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(ShowEmptyConflictRows));
        OnPropertyChanged(nameof(EmptyConflictText));
        OnPropertyChanged(nameof(ShowConflictPlaceholder));
        StartConflictScanCommand?.RaiseCanExecuteChanged();
        CancelConflictScanCommand?.RaiseCanExecuteChanged();
    }

    private async Task StartConflictScanAsync()
    {
        RefreshAvailability();
        SaveSettings();
        var sources = Sources.Where(source => source.IsEnabled)
            .Select((source, index) => new ScanSource(source.Path, source.Path, true, index, source.Name)).ToArray();
        ResetConflictSession();
        var generation = conflictGeneration;
        var scanningSession = conflictSession;
        var initialRequest = ++conflictRowsRequest;
        using var cancellation = new CancellationTokenSource();
        conflictCancellation = cancellation;
        conflictRunState = "scanning";
        conflictStatus = "正在查找 package 文件…";
        IsBusy = true;
        RefreshConflictResults();
        var activeStage = 0;
        var reportedStage = -1;

        void ClearProgress()
        {
            conflictCompleted = 0;
            conflictProgressMaximum = 1;
            conflictProgressTotal = null;
            conflictIndeterminate = true;
            OnPropertyChanged(nameof(ConflictCompleted));
            OnPropertyChanged(nameof(ConflictProgressMaximum));
            OnPropertyChanged(nameof(ConflictProgressText));
            OnPropertyChanged(nameof(ConflictIndeterminate));
        }

        void Report(int stage, string phase, int completed, int? total)
        {
            void Apply()
            {
                if (generation != conflictGeneration || conflictRunState != "scanning"
                    || stage != Volatile.Read(ref activeStage)) return;
                // 进入新阶段先清掉上一阶段的数字，总数未知时只留阶段名称。
                if (stage != reportedStage)
                {
                    reportedStage = stage;
                    ClearProgress();
                }
                conflictStatus = phase;
                var safeTotal = total is > 0 ? total.Value : 0;
                conflictProgressTotal = total is > 0 ? total : null;
                conflictProgressMaximum = Math.Max(1, safeTotal);
                conflictCompleted = Math.Min(completed, safeTotal);
                conflictIndeterminate = total is null or <= 0;
                OnPropertyChanged(nameof(ConflictStatus));
                OnPropertyChanged(nameof(ConflictCompleted));
                OnPropertyChanged(nameof(ConflictProgressMaximum));
                OnPropertyChanged(nameof(ConflictProgressText));
                OnPropertyChanged(nameof(ConflictIndeterminate));
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Apply);
            else Apply();
        }

        var packageProgress = new Progress<PackageConflictScanProgress>(p => Report(0,
            p.Phase == PackageConflictScanPhase.Discovering ? "正在查找资源文件…" : "正在读取资源索引…", p.CompletedItems, p.TotalItems));
        var contentProgress = new Progress<ResourceContentComparisonProgress>(p => Report(1, "正在比较资源…", p.CompletedResources,
            p.Phase == ResourceContentComparisonPhase.ReadingContents ? p.TotalResources : null));
        var scriptProgress = new Progress<ScriptModuleScanProgress>(p => Report(2,
            p.Phase == ScriptModuleScanPhase.Discovering ? "正在查找脚本文件…" : "正在比较同名脚本…", p.CompletedItems, p.TotalItems));

        var initialView = conflictView;
        var initialFilter = conflictFilter;
        try
        {
            var results = await Task.Run(async () =>
            {
                var packages = await packageConflictScanner.ScanAsync(new(sources), packageProgress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref activeStage, 1);
                var contents = await resourceContentComparer.CompareAsync(new(packages.Candidates), contentProgress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref activeStage, 2);
                var scripts = await scriptModuleScanner.ScanAsync(new(sources), scriptProgress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref activeStage, 3);
                Report(3, "正在整理文件对…", 0, null);
                var mapped = new ConflictResultSession();
                mapped.Replace(packages, contents, scripts, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var view = mapped.Resources.Count == 0 && mapped.Scripts.Count == 0
                    && (mapped.Problems.Count > 0 || mapped.RangeProblems.Count > 0) ? "problems" : initialView;
                var rows = mapped.BuildVisibleRows(view, initialFilter, mapped.GetExpandedKeysSnapshot(), cancellation.Token);
                return (Session: mapped, Rows: rows, ParentCount: rows.Count(row => row.Depth == 0), View: view);
            }, cancellation.Token);
            if (generation != conflictGeneration) return;
            cancellation.Token.ThrowIfCancellationRequested();
            // 扫描期间发生的视图请求也在发布前完成，统一发布完整会话。
            var finalView = results.View == "problems" ? "problems" : conflictView;
            var finalFilter = conflictFilter;
            var initialRows = results.Rows;
            var parentCount = results.ParentCount;
            while (finalView != results.View || finalFilter != initialFilter)
            {
                var viewSnapshot = finalView;
                var filterSnapshot = finalFilter;
                var expandedSnapshot = results.Session.GetExpandedKeysSnapshot();
                var projection = await Task.Run(() =>
                {
                    var rows = results.Session.BuildVisibleRows(viewSnapshot, filterSnapshot, expandedSnapshot, cancellation.Token);
                    return (Rows: rows, ParentCount: rows.Count(row => row.Depth == 0));
                }, cancellation.Token);
                initialRows = projection.Rows;
                parentCount = projection.ParentCount;
                if (generation != conflictGeneration) return;
                if ((results.View == "problems" || finalView == conflictView) && finalFilter == conflictFilter) break;
                finalView = conflictView;
                finalFilter = conflictFilter;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != conflictGeneration || initialRequest != conflictRowsRequest
                || !ReferenceEquals(scanningSession, conflictSession)) return;
            conflictSession = results.Session;
            conflictView = finalView;
            OnPropertyChanged(nameof(ConflictView));
            PublishConflictRows(initialRows, parentCount);
            conflictRunState = "complete";
            conflictStatus = HasConflictMatches ? "扫描完成" : "已分析范围未发现冲突";
            ClearProgress();
            conflictPhase = HasConflictProblems
                ? $"{conflictSession.Problems.Count} 个文件未完成分析 · {conflictSession.RangeProblems.Count} 项范围问题"
                : string.Empty;
            RefreshConflictVisibility();
        }
        catch (OperationCanceledException)
        {
            if (generation != conflictGeneration) return;
            conflictRunState = "cancelled";
            conflictStatus = "扫描已取消";
            ClearProgress();
            conflictPhase = "本次分析未完成";
        }
        catch (Exception exception)
        {
            if (generation != conflictGeneration) return;
            ReportConflictFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(conflictCancellation, cancellation))
            {
                conflictCancellation = null;
                IsBusy = false;
                RefreshConflictResults();
            }
        }
    }

    private void ReportConflictFailure(Exception exception)
    {
        conflictRunState = "failed";
        conflictStatus = "扫描没有完成";
        conflictPhase = exception is UnauthorizedAccessException or System.IO.IOException
            ? "请检查来源后重试" : "请重试扫描";
        conflictCompleted = 0;
        conflictProgressMaximum = 1;
        conflictProgressTotal = null;
        conflictIndeterminate = true;
        RefreshConflictResults();
    }
}
