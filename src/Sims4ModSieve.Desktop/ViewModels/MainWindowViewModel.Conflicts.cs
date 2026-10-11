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
    private string conflictSection = "overview";
    private string conflictView = "resources";
    private string conflictFilter = "all";
    private string conflictStatus = "尚未扫描";
    private string conflictPhase = string.Empty;
    private string conflictRunState = "initial";
    private int conflictCompleted;
    private int conflictTotal = 1;
    private bool conflictIndeterminate = true;

    public RelayCommand OpenConflictSectionCommand { get; private set; } = null!;
    public RelayCommand ConflictBackCommand { get; private set; } = null!;
    public RelayCommand SelectConflictViewCommand { get; private set; } = null!;
    public RelayCommand SelectConflictFilterCommand { get; private set; } = null!;
    public AsyncRelayCommand StartConflictScanCommand { get; private set; } = null!;
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
    public bool IsPartialFilterSelected => conflictFilter == "partial";
    public string ConflictStatus => conflictStatus;
    public string ConflictPhase => conflictPhase;
    public string ConflictRunState => conflictRunState;
    public bool IsConflictScanning => conflictRunState == "scanning";
    public bool IsConflictNotScanning => !IsConflictScanning;
    public bool ShowConflictFilters => IsConflictBasic && conflictView != "problems";
    public bool IsConflictComplete => conflictRunState == "complete";
    public bool ShowConflictPlaceholder => IsConflictBasic && !IsConflictComplete;
    public bool HasConflictProblems => conflictSession.Problems.Count > 0 || conflictSession.RangeProblems.Count > 0;
    public bool ShowConflictWarning => IsConflictComplete && HasConflictProblems;
    public bool HasConflictMatches => conflictSession.Resources.Count > 0 || conflictSession.Scripts.Count > 0;
    public IReadOnlyList<ConflictGroupRow> ConflictResources => conflictSession.Resources;
    public IReadOnlyList<ConflictScriptGroupRow> ConflictScripts => conflictSession.Scripts;
    public IReadOnlyList<ConflictProblemRow> ConflictProblems => conflictSession.Problems;
    public IReadOnlyList<ConflictRangeProblem> ConflictRangeProblems => conflictSession.RangeProblems;
    public IReadOnlyList<ConflictGroupRow> VisibleConflictResources =>
        conflictSession.Resources.Where(MatchesConflictFilter).ToArray();
    public bool ShowEmptyConflictResources => ShowConflictResources && IsConflictComplete && VisibleConflictResources.Count == 0;
    public IReadOnlyList<ConflictScriptGroupRow> VisibleConflictScripts => conflictSession.Scripts
        .Select(group => new ConflictScriptGroupRow(group.Name, group.Modules.Where(MatchesConflictFilter).ToArray()))
        .Where(group => group.Modules.Count > 0).ToArray();
    public bool ShowEmptyConflictScripts => ShowConflictScripts && IsConflictComplete && VisibleConflictScripts.Count == 0;
    public bool ShowConflictResources => IsConflictBasic && conflictView == "resources";
    public bool ShowConflictScripts => IsConflictBasic && conflictView == "scripts";
    public bool ShowConflictProblems => IsConflictBasic && conflictView == "problems";
    public int ConflictCompleted => conflictCompleted;
    public int ConflictTotal => conflictTotal;
    public bool ConflictIndeterminate => conflictIndeterminate;

    private void InitializeConflict()
    {
        OpenConflictSectionCommand = new RelayCommand(section => SelectConflictSection(section as string), _ => !IsBusy);
        ConflictBackCommand = new RelayCommand(_ => SelectConflictSection("overview"), _ => !IsBusy);
        SelectConflictViewCommand = new RelayCommand(view => SelectConflictView(view as string));
        SelectConflictFilterCommand = new RelayCommand(filter => SelectConflictFilter(filter as string));
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
        OnPropertyChanged(nameof(ShowEmptyConflictResources));
        OnPropertyChanged(nameof(ShowEmptyConflictScripts));
        OnPropertyChanged(nameof(ShowConflictPlaceholder));
    }

    private void SelectConflictView(string? view)
    {
        if (view is not ("resources" or "scripts" or "problems")) return;
        conflictView = view;
        OnPropertyChanged(nameof(ConflictView));
        RefreshConflictVisibility();
    }

    private void SelectConflictFilter(string? filter)
    {
        if (filter is not ("all" or "different" or "same" or "partial")) return;
        conflictFilter = filter;
        OnPropertyChanged(nameof(ConflictFilter));
        OnPropertyChanged(nameof(VisibleConflictResources));
        OnPropertyChanged(nameof(VisibleConflictScripts));
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsDifferentFilterSelected));
        OnPropertyChanged(nameof(IsSameFilterSelected));
        OnPropertyChanged(nameof(IsPartialFilterSelected));
        OnPropertyChanged(nameof(ShowEmptyConflictResources));
        OnPropertyChanged(nameof(ShowEmptyConflictScripts));
        OnPropertyChanged(nameof(ShowConflictPlaceholder));
    }

    private bool MatchesConflictFilter(ConflictGroupRow group) => conflictFilter switch
    {
        "different" => group.IsDifferent,
        "same" => group.IsIdentical,
        "partial" => group.IsPartial,
        _ => true,
    };

    private void ResetConflictSession()
    {
        conflictGeneration++;
        conflictCancellation?.Cancel();
        conflictSession.Reset();
        conflictView = "resources";
        conflictFilter = "all";
        conflictRunState = "initial";
        conflictStatus = "尚未扫描";
        conflictPhase = string.Empty;
        conflictCompleted = 0;
        conflictTotal = 1;
        conflictIndeterminate = true;
        OnPropertyChanged(nameof(ConflictView));
        OnPropertyChanged(nameof(ConflictFilter));
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsDifferentFilterSelected));
        OnPropertyChanged(nameof(IsSameFilterSelected));
        OnPropertyChanged(nameof(IsPartialFilterSelected));
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
            nameof(ConflictProblems), nameof(ConflictRangeProblems), nameof(VisibleConflictResources),
            nameof(VisibleConflictScripts), nameof(ConflictCompleted), nameof(ConflictTotal),
            nameof(ConflictIndeterminate) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(ShowEmptyConflictResources));
        OnPropertyChanged(nameof(ShowEmptyConflictScripts));
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
        using var cancellation = new CancellationTokenSource();
        conflictCancellation = cancellation;
        conflictRunState = "scanning";
        conflictStatus = "正在查找 package 文件…";
        IsBusy = true;
        RefreshConflictResults();
        var activeStage = 0;

        void Report(int stage, string phase, int completed, int? total)
        {
            void Apply()
            {
                if (generation != conflictGeneration || conflictRunState != "scanning"
                    || stage != Volatile.Read(ref activeStage)) return;
                conflictPhase = phase;
                conflictStatus = phase;
                conflictIndeterminate = total is null;
                conflictTotal = Math.Max(1, total ?? 1);
                conflictCompleted = Math.Min(completed, conflictTotal);
                OnPropertyChanged(nameof(ConflictPhase));
                OnPropertyChanged(nameof(ConflictStatus));
                OnPropertyChanged(nameof(ConflictIndeterminate));
                OnPropertyChanged(nameof(ConflictTotal));
                OnPropertyChanged(nameof(ConflictCompleted));
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Apply);
            else Apply();
        }

        var packageProgress = new Progress<PackageConflictScanProgress>(p => Report(0, p.Message, p.CompletedItems, p.TotalItems));
        var contentProgress = new Progress<ResourceContentComparisonProgress>(p => Report(1, p.Message, p.CompletedResources,
            p.Phase == ResourceContentComparisonPhase.ReadingContents ? p.TotalResources : null));
        var scriptProgress = new Progress<ScriptModuleScanProgress>(p => Report(2, p.Message, p.CompletedItems, p.TotalItems));

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
                var mapped = new ConflictResultSession();
                mapped.Replace(packages, contents, scripts);
                cancellation.Token.ThrowIfCancellationRequested();
                return mapped;
            }, cancellation.Token);
            if (generation != conflictGeneration) return;
            conflictSession = results;
            conflictRunState = "complete";
            conflictStatus = HasConflictMatches ? "扫描完成" : "已分析范围未发现冲突";
            conflictPhase = HasConflictProblems
                ? $"{conflictSession.Problems.Count} 个文件未完成分析 · {conflictSession.RangeProblems.Count} 项范围问题"
                : string.Empty;
            if (!HasConflictMatches && HasConflictProblems) SelectConflictView("problems");
        }
        catch (OperationCanceledException)
        {
            if (generation != conflictGeneration) return;
            conflictRunState = "cancelled";
            conflictStatus = "扫描已取消";
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
        RefreshConflictResults();
    }
}
