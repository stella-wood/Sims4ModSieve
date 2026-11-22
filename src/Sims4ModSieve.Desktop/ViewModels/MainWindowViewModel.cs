using Sims4ModSieve.Core.Settings;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Sims4ModSieve.Caching;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Reporting;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDuplicateRunService duplicateRunService;
    private readonly DuplicateResultSession resultSession;
    private readonly IFolderPickerService folderPicker;
    private readonly IDuplicateSettingsStore settingsStore;
    private readonly IFileActionService fileActionService;
    private readonly IFileActionDialogService fileActionDialogs;
    private readonly long? conflictGroupingMemoryBudgetBytes;
    private readonly string? scanCacheDirectory;
    private readonly IUnexpectedErrorHandler unexpectedErrorHandler;
    private CancellationTokenSource? scanCancellation;
    private IReadOnlyList<DuplicateGroup>? lastDeleteSnapshot;
    private int resultGeneration;
    private int lastDeleteResultGeneration;
    private bool isHomeVisible = true;
    private bool isScannerVisible;
    private bool isConflictVisible;
    private bool isBusy;
    private bool isProgressIndeterminate;
    private string? modsRoot;
    private bool isModsRootAvailable;
    private bool isStatusVisible;
    private string statusText = string.Empty;
    private string statusDetail = string.Empty;
    private double progressValue;
    private double progressMaximum = 1;
    private int discoveredFileCount;
    private int issueCount;
    private string fileActionStatusText = string.Empty;

    public MainWindowViewModel(
        IDuplicateRunService duplicateRunService,
        IFolderPickerService folderPicker,
        IDuplicateSettingsStore settingsStore,
        IFileActionService? fileActionService = null,
        IFileActionDialogService? fileActionDialogs = null,
        IUnexpectedErrorHandler? unexpectedErrorHandler = null,
        DuplicateResultSession? resultSession = null,
        IPackageConflictScanner? packageConflictScanner = null,
        IResourceContentComparer? resourceContentComparer = null,
        IScriptModuleScanner? scriptModuleScanner = null,
        SettingsSupportService? settingsSupport = null,
        Action<string>? conflictDetailsCopy = null,
        FileManagementStore? fileManagementStore = null,
        Func<IReadOnlyList<string>>? pickIgnoredFiles = null,
        ModDisableService? modDisableService = null,
        Func<IReadOnlyList<string>>? pickDisabledFiles = null,
        Func<string, bool>? confirmModChange = null,
        Func<DownloadedUpdate, Task>? startUpdate = null,
        long? conflictGroupingMemoryBudgetBytes = null,
        string? scanCacheDirectory = null)
    {
        this.duplicateRunService = duplicateRunService;
        this.conflictDetailsCopy = conflictDetailsCopy ?? System.Windows.Clipboard.SetText;
        this.resultSession = resultSession ?? new DuplicateResultSession();
        this.folderPicker = folderPicker;
        this.settingsStore = settingsStore;
        this.settingsSupport = settingsSupport ?? new SettingsSupportService();
        this.fileManagementStore = fileManagementStore ?? new FileManagementStore(
            settingsStore is DuplicateSettingsStore diskSettings ? diskSettings.DataDirectory : null);
        this.pickIgnoredFiles = pickIgnoredFiles ?? PickIgnoredFiles;
        this.modDisableService = modDisableService ?? new ModDisableService(
            settingsStore is DuplicateSettingsStore diskModSettings ? diskModSettings.DataDirectory
                : Path.Combine(Path.GetTempPath(), "Sims4ModSieve", Guid.NewGuid().ToString("N")),
            logProtectedPath: UnexpectedErrorHandler.LogProtectedPath);
        this.pickDisabledFiles = pickDisabledFiles ?? PickDisabledFiles;
        this.confirmModChange = confirmModChange;
        this.fileActionService = fileActionService ?? new FileActionService(new WindowsRecycleBinAdapter(),
            new PhysicalFileMetadataAccess(), this.fileManagementStore, this.modDisableService,
            logProtectedPath: UnexpectedErrorHandler.LogProtectedPath);
        this.fileActionDialogs = fileActionDialogs ?? new FileActionDialogService();
        this.conflictGroupingMemoryBudgetBytes = conflictGroupingMemoryBudgetBytes;
        this.scanCacheDirectory = scanCacheDirectory;
        this.unexpectedErrorHandler = unexpectedErrorHandler ?? new UnexpectedErrorHandler();
        this.packageConflictScanner = packageConflictScanner ?? new PackageConflictScanner(
            new PhysicalFileSystemAccess(), Sims4ModSieve.Packages.LlamaLogicPackageIndexReader.CreateDefault());
        this.resourceContentComparer = resourceContentComparer;
        this.scriptModuleScanner = scriptModuleScanner ?? new ScriptModuleScanner(new PhysicalFileSystemAccess());
        this.resultSession.PropertyChanged += OnResultSessionPropertyChanged;

        Sources.CollectionChanged += OnSourcesChanged;
        ConflictSources.CollectionChanged += OnSourcesChanged;
        OpenDuplicateScannerCommand = new RelayCommand(_ => ShowScanner(), _ => !IsBusy);
        OpenConflictCommand = new RelayCommand(_ => ShowConflict(), _ => !IsBusy);
        GoHomeCommand = new RelayCommand(_ => ShowHome(), _ => !IsBusy);
        AddSourceCommand = new RelayCommand(_ => AddSource(), _ => !IsBusy);
        ScanLinkTargetCommand = new RelayCommand(ScanLinkTarget,
            parameter => !IsBusy && parameter is string target && CanAddLinkTarget(target, ActiveSources));
        ChangeModsRootCommand = new RelayCommand(_ => ChangeModsRoot(), _ => !IsBusy);
        RemoveSourceCommand = new RelayCommand(RemoveSource, _ => !IsBusy);
        StartScanCommand = new AsyncRelayCommand(
            StartScanAsync,
            exception => ReportUnexpectedError("扫描没有完成", exception),
            CanStartScan);
        CancelScanCommand = new RelayCommand(_ => scanCancellation?.Cancel(), _ => IsBusy);
        ApplySuggestionsCommand = new RelayCommand(_ => ApplySuggestions(), _ => VisibleDuplicateGroups.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => SelectedFileCount > 0 && !IsBusy);
        ToggleAllGroupsCommand = new RelayCommand(_ => ToggleAllGroups(), _ => VisibleDuplicateGroups.Count > 0 && !IsBusy);
        DeleteSelectedCommand = new AsyncRelayCommand(
            DeleteSelectedAsync,
            exception => ReportUnexpectedError("删除没有完成", exception),
            () => SelectedFileCount > 0 && !IsBusy);
        UndoLastDeleteCommand = new AsyncRelayCommand(
            UndoLastDeleteAsync,
            exception => ReportUnexpectedError("撤回没有完成", exception),
            () => this.fileActionService.CanUndoLastDelete && !IsBusy);

        LoadSettings();
        InitializeConflict();
        InitializeSettings();
        InitializeUpdates(startUpdate);
        InitializeFileManagement();
    }

    public ObservableCollection<ScanSourceViewModel> Sources { get; } = [];

    public ObservableCollection<ScanSourceViewModel> ConflictSources { get; } = [];

    private ObservableCollection<ScanSourceViewModel> ActiveSources => IsConflictVisible ? ConflictSources : Sources;

    public IReadOnlyList<DuplicateGroupViewModel> Groups => resultSession.Groups;
    public IReadOnlyList<DuplicateGroupViewModel> VisibleDuplicateGroups => resultSession.VisibleDuplicateGroups;
    public string DuplicatePathQuery { get => resultSession.DuplicatePathQuery; set => resultSession.DuplicatePathQuery = value; }
    public PathSearch DuplicateSearch => resultSession.DuplicateSearch;
    public string DuplicateSearchStatus => resultSession.DuplicateSearchStatus;
    public bool ShowDuplicatePlaceholder => VisibleDuplicateGroups.Count == 0;
    public bool CanSearchDuplicateResults => !IsBusy && (HasResults || IsStatusVisible);

    public RelayCommand OpenDuplicateScannerCommand { get; }
    public RelayCommand OpenConflictCommand { get; }

    public RelayCommand GoHomeCommand { get; }

    public RelayCommand AddSourceCommand { get; }

    public RelayCommand ChangeModsRootCommand { get; }

    public RelayCommand RemoveSourceCommand { get; }

    public AsyncRelayCommand StartScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand ApplySuggestionsCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand ToggleAllGroupsCommand { get; }

    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public AsyncRelayCommand UndoLastDeleteCommand { get; }

    public bool IsHomeVisible
    {
        get => isHomeVisible;
        private set => SetProperty(ref isHomeVisible, value);
    }

    public bool IsScannerVisible
    {
        get => isScannerVisible;
        private set => SetProperty(ref isScannerVisible, value);
    }

    public bool IsConflictVisible
    {
        get => isConflictVisible;
        private set
        {
            if (SetProperty(ref isConflictVisible, value))
            {
                RefreshConflictVisibility();
                ScanLinkTargetCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEditSources));
            OnPropertyChanged(nameof(CanEditFileManagement));
            OnPropertyChanged(nameof(CanSearchDuplicateResults));
            RefreshCommands();
        }
    }

    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => SetProperty(ref isProgressIndeterminate, value);
    }

    public bool IsStatusVisible
    {
        get => isStatusVisible;
        private set
        {
            if (!SetProperty(ref isStatusVisible, value)) return;
            OnPropertyChanged(nameof(DuplicatePlaceholderText));
            OnPropertyChanged(nameof(CanSearchDuplicateResults));
        }
    }

    public string? ModsRoot
    {
        get => modsRoot;
        private set
        {
            if (SetProperty(ref modsRoot, value))
            {
                OnPropertyChanged(nameof(ModsRootDisplay));
                OnPropertyChanged(nameof(HasModsRoot));
                OnPropertyChanged(nameof(ModsRootActionText));
                RefreshApplicationLocationWarning();
            }
        }
    }

    public string ModsRootDisplay => string.IsNullOrWhiteSpace(ModsRoot) ? "未设置" : ModsRoot;

    public bool HasModsRoot => !string.IsNullOrWhiteSpace(ModsRoot);

    public string ModsRootActionText => HasModsRoot ? "更改" : "选择";

    public bool IsModsRootAvailable
    {
        get => isModsRootAvailable;
        private set
        {
            if (SetProperty(ref isModsRootAvailable, value))
            {
                OnPropertyChanged(nameof(HasModsRootAvailabilityWarning));
            }
        }
    }

    public bool HasModsRootAvailabilityWarning => HasModsRoot && !IsModsRootAvailable;

    public string StatusText
    {
        get => statusText;
        private set
        {
            if (SetProperty(ref statusText, value)) OnPropertyChanged(nameof(DuplicatePlaceholderText));
        }
    }

    public string DuplicatePlaceholderText => DuplicateSearch.IsActive ? "没找到相关文件"
        : IsStatusVisible ? StatusText : "尚未扫描";

    public string StatusDetail
    {
        get => statusDetail;
        private set => SetProperty(ref statusDetail, value);
    }

    public double ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public double ProgressMaximum
    {
        get => progressMaximum;
        private set => SetProperty(ref progressMaximum, value);
    }

    public int DiscoveredFileCount
    {
        get => discoveredFileCount;
        private set => SetProperty(ref discoveredFileCount, value);
    }

    public int IssueCount
    {
        get => issueCount;
        private set => SetProperty(ref issueCount, value);
    }

    public bool HasResults => resultSession.HasResults;

    public bool CanEditSources => !IsBusy;

    public bool HasNoResults => resultSession.HasNoResults;

    public int DuplicateGroupCount => resultSession.DuplicateGroupCount;

    public int DuplicateFileCount => resultSession.DuplicateFileCount;

    public int SelectedFileCount => resultSession.SelectedFileCount;

    public string DuplicateSizeText => resultSession.DuplicateSizeText;

    public string SelectedSizeText => resultSession.SelectedSizeText;

    public bool ShowExpandAllIcon => resultSession.ShowExpandAllIcon;

    public bool ShowCollapseAllIcon => resultSession.ShowCollapseAllIcon;

    public string ExpandCollapseToolTip => resultSession.ExpandCollapseToolTip;

    public string FileActionStatusText
    {
        get => fileActionStatusText;
        private set
        {
            if (SetProperty(ref fileActionStatusText, value))
            {
                OnPropertyChanged(nameof(HasFileActionStatus));
            }
        }
    }

    public bool HasFileActionStatus => !string.IsNullOrWhiteSpace(FileActionStatusText);

    public bool CanUndoLastDelete => lastDeleteOwner != "conflict" && fileActionService.CanUndoLastDelete;

    private void LoadSettings()
    {
        ReadSavedSettings();

        if (HasModsRoot)
        {
            AddSource(Sources, ModsRoot!);
            AddSource(ConflictSources, ModsRoot!);
        }

        RefreshAvailability();
    }

    public void RefreshSavedFolders()
    {
        if (!IsBusy && settingsStore is DuplicateSettingsStore) ReadSavedSettings();
    }

    private void ReadSavedSettings()
    {
        try
        {
            ApplySavedSettings(settingsStore.Load());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsFeedback = "无法读取已保存的设置";
        }
    }

    private void ApplySavedSettings(DuplicateSettings? settings)
    {
        var concurrency = settings?.HashConcurrency ?? DuplicateHashConcurrency.Auto;
        if (!Enum.IsDefined(concurrency)) concurrency = DuplicateHashConcurrency.Auto;
        SetProperty(ref duplicateHashConcurrency, concurrency, nameof(DuplicateHashConcurrency));
        ModsRoot = NormalizePathOrNull(settings?.ModsRoot) ?? settingsSupport.FindDefaultFolder("Mods");
        TrayRoot = NormalizePathOrNull(settings?.TrayRoot);
        RefreshAvailability();
    }

    private void ShowScanner()
    {
        IsSettingsVisible = false;
        RefreshAvailability();
        IsHomeVisible = false;
        IsConflictVisible = false;
        IsScannerVisible = true;
    }

    private void ShowHome()
    {
        IsSettingsVisible = false;
        IsScannerVisible = false;
        IsConflictVisible = false;
        IsHomeVisible = true;
    }

    private void AddSource()
    {
        var path = folderPicker.PickFolder("选择要检查的文件夹", ModsRoot);
        if (path is not null)
        {
            AddSource(ActiveSources, path);
        }
    }

    private void ChangeModsRoot()
    {
        var path = folderPicker.PickFolder("选择当前游戏的 Mods 文件夹", ModsRoot);
        var normalizedPath = NormalizePathOrNull(path);
        if (normalizedPath is null)
        {
            return;
        }

        if (!TrySaveFolder("Mods", normalizedPath))
        {
            fileActionDialogs.ShowProblems("设置未保存", [new FileActionFailure(string.Empty, SettingsFeedback)]);
            return;
        }

        ModsRoot = normalizedPath;
        var source = ActiveSources.FirstOrDefault(item => PathRulesEqual(item.Path, normalizedPath));
        if (source is null)
        {
            AddSource(ActiveSources, normalizedPath);
        }
        else
        {
            source.IsEnabled = true;
        }

        RefreshAvailability();
    }

    private void AddSource(ObservableCollection<ScanSourceViewModel> sources, string path)
    {
        var normalizedPath = NormalizePathOrNull(path);
        if (normalizedPath is null
            || sources.Any(source => PathRulesEqual(source.Path, normalizedPath)))
        {
            return;
        }

        sources.Add(new ScanSourceViewModel(normalizedPath));
    }

    private void RemoveSource(object? parameter)
    {
        if (parameter is not ScanSourceViewModel source)
        {
            return;
        }

        ActiveSources.Remove(source);
    }

    private bool CanStartScan() => !IsBusy && Sources.Any(source => source.IsEnabled);

    internal void CancelActiveScan()
    {
        scanCancellation?.Cancel();
        conflictCancellation?.Cancel();
    }

    private async Task<CachingStableFileHasher?> OpenScanCacheAsync() => scanCacheDirectory is { } directory
        ? await Task.Run(() => CachingStableFileHasher.Open(directory)) : null;

    private async Task<DuplicateReport> RunDuplicateScanAsync(DuplicateRunInput input,
        IProgress<DuplicateScanProgress> progress, CachingStableFileHasher? cache, CancellationToken token)
    {
        if (cache is null) return await duplicateRunService.RunAsync(input, progress, token);
        return await cache.RunAsync(hasher => duplicateRunService.RunAsync(
            input with { FileHasher = hasher, Coverage = cache.Coverage }, progress, token), token);
    }

    private static async Task CommitScanCacheAsync(CachingStableFileHasher? cache, CancellationToken token)
    {
        if (cache is null) return;
        // Results are already published. Closing now still cancels any pending cache write.
        try { await Task.Run(() => cache.Commit(token), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task StartScanAsync()
    {
        RefreshAvailability();
        var enabledSources = Sources.Where(source => source.IsEnabled).ToArray();
        if (enabledSources.Length == 0)
        {
            return;
        }

        var storage = new WindowsStorageProfile();
        var input = new DuplicateRunInput(
            enabledSources
                .Select(source => new DuplicateRunSource(source.Path, source.Name))
                .ToArray(),
            ModsRoot)
        {
            HashConcurrency = DuplicateHashConcurrency,
            StorageProfile = storage,
        };

        using var performance = ScanPerformance.Start("重复检测");
        var timingOutcome = "未完成";
        ScanPerformance.Stage? renderingTiming = null;
        ResetResultSession();
        var generation = resultGeneration;
        scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsStatusVisible = true;
        StatusText = "正在准备扫描…";
        StatusDetail = string.Empty;

        var progressGate = new object();
        var acceptProgress = true;
        var progress = new Progress<DuplicateScanProgress>(value =>
        {
            lock (progressGate)
            {
                if (acceptProgress && generation == resultGeneration) UpdateProgress(value);
            }
        });

        try
        {
            using var cache = await OpenScanCacheAsync();
            DuplicateReport report;
            try
            {
                report = await Task.Run(
                    () => RunDuplicateScanAsync(input, progress, cache, scanCancellation.Token),
                    scanCancellation.Token);
            }
            finally
            {
                lock (progressGate) acceptProgress = false;
            }

            if (generation != resultGeneration) return;
            StatusText = "正在整理结果…";
            IsProgressIndeterminate = true;
            IReadOnlyList<DuplicateGroupViewModel> resultGroups;
            using (var preparationTiming = ScanPerformance.Measure("界面 · 生成重复结果模型"))
            {
                resultGroups = await Task.Run(
                    () => resultSession.PrepareGroups(report.Groups, scanCancellation.Token),
                    scanCancellation.Token);
                if (preparationTiming is not null)
                {
                    preparationTiming.Files = report.Groups.Sum(group => (long)group.Files.Count);
                    preparationTiming.Bytes = report.Groups.Sum(group => group.FileSize * group.Files.Count);
                    preparationTiming.ResultItems = resultGroups.Count;
                }
            }
            if (generation != resultGeneration) return;
            scanCancellation.Token.ThrowIfCancellationRequested();
            renderingTiming = BeginResultRenderingTiming();
            if (renderingTiming is not null)
            {
                renderingTiming.Files = report.Groups.Sum(group => (long)group.Files.Count);
                renderingTiming.Bytes = report.Groups.Sum(group => group.FileSize * group.Files.Count);
                renderingTiming.ResultItems = resultGroups.Count;
            }
            resultSession.Replace(resultGroups);

            IssueCount = report.Issues.Count;
            DiscoveredFileCount = report.DiscoveredFileCount;
            StatusText = report.DiscoveredFileCount == 0 && report.Issues.Count == 0
                ? "这个文件夹里没有找到 mod 文件"
                : report.DiscoveredFileCount == 0 ? "扫描未完成，请查看原因" : report.Groups.Count == 0
                ? "扫描完成，没有发现重复文件"
                : $"扫描完成，发现 {report.Groups.Count} 组重复文件";
            var firstIssue = report.Issues.FirstOrDefault();
            DuplicateLinkTarget = firstIssue?.LinkTarget;
            StatusDetail = firstIssue is null
                ? $"共查看 {report.DiscoveredFileCount:N0} 个文件，结果完整。"
                : $"共查看 {report.DiscoveredFileCount:N0} 个文件；有 {report.Issues.Count} 项未完成检查。\n"
                    + (firstIssue.Code.EndsWith("-failed", StringComparison.Ordinal) ? "无法读取：" : "未完成检查：")
                    + $"{firstIssue.Path}\n{firstIssue.Message}";
            timingOutcome = "完成";
            await CommitScanCacheAsync(cache, scanCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            timingOutcome = "已取消";
            if (generation == resultGeneration)
            {
                StatusText = "扫描已取消";
                StatusDetail = string.Empty;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            timingOutcome = "失败";
            if (generation == resultGeneration)
            {
                StatusText = "扫描没有完成";
                StatusDetail = exception.Message;
            }
        }
        finally
        {
            scanCancellation.Dispose();
            scanCancellation = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            RefreshResultProperties();
            await FinishScanTimingAsync(performance, timingOutcome, renderingTiming, storage);
        }
    }

    private void UpdateProgress(DuplicateScanProgress progress)
    {
        StatusText = progress.Phase == DuplicateScanPhase.Hashing && progress.Message.EndsWith('）')
            ? $"{progress.Message}…" : progress.Message;
        DiscoveredFileCount = progress.DiscoveredFileCount;
        IsProgressIndeterminate = progress.TotalItems is null || progress.Phase == DuplicateScanPhase.Discovering;
        if (progress.TotalItems is int total)
        {
            ProgressMaximum = Math.Max(1, total);
            ProgressValue = Math.Min(progress.CompletedItems, ProgressMaximum);
        }

        StatusDetail = progress.Phase switch
        {
            DuplicateScanPhase.Discovering => $"已经找到 {progress.DiscoveredFileCount:N0} 个候选文件",
            DuplicateScanPhase.Hashing => string.Empty,
            _ => StatusDetail,
        };
    }

    private void ApplySuggestions() => resultSession.ApplySuggestions();

    private void ClearSelection() => resultSession.ClearSelection();

    private void ToggleAllGroups() => resultSession.ToggleAllGroups();

    private Task DeleteSelectedAsync() => DeleteSelectedCoreAsync();

    private async Task DeleteSelectedCoreAsync()
    {
        if (IsBusy) return;
        var remaining = await RefreshMissingResultMembersAsync(
            Groups.SelectMany(group => group.Files).Where(file => file.IsSelected).Select(file => file.Path), fromConflicts: false);
        if (remaining.Count == 0) return;
        var selectedFiles = Groups
            .SelectMany(group => group.Files
                .Where(file => file.IsSelected && remaining.Contains(file.Path))
                .Select(file => new FileDeletionCandidate(
                    file.Path,
                    file.Size,
                    file.LastWriteTimeUtc,
                    group.GroupLabel,
                    ExpectedIdentity: file.Identity,
                    ExpectedSha256: group.Model.Sha256)))
            .ToArray();
        var fullySelectedGroups = Groups
            .Where(group => group.SelectedCount == group.Files.Count)
            .Select(group => group.GroupLabel)
            .ToArray();
        var snapshot = Groups.Select(group => group.Model).ToArray();
        var deletionGeneration = resultGeneration;
        var relatedConflictSession = conflictSession;
        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() => fileActionService.Preflight(selectedFiles, fullySelectedGroups));
            if (!preflight.CanExecute)
            {
                fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems);
                return;
            }
            if (!fileActionDialogs.ConfirmDeletion(preflight)) return;

            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                if (deletionGeneration == resultGeneration)
                    RememberDeletion("duplicates", relatedConflictSession, snapshot, deletionGeneration, result.CompletedPaths);
                else fileActionService.ClearLastDelete();
                if (ReferenceEquals(relatedConflictSession, conflictSession) && IsConflictComplete)
                {
                    await UpdateConflictFilePathsAsync(relatedConflictSession, result.CompletedPaths, restore: false);
                }
                if (deletionGeneration == resultGeneration)
                {
                    resultSession.RemoveCompletedPaths(snapshot, result.CompletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
                    FileActionStatusText = $"已移入回收站 {result.CompletedPaths.Count} 个文件";
                }
            }

            if (result.Failures.Count > 0)
            {
                fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
            }
        }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
        }
    }

    private Task UndoLastDeleteAsync() => UndoLastDeleteCoreAsync();

    private async Task UndoLastDeleteCoreAsync()
    {
        if (!fileActionService.CanUndoLastDelete)
        {
            return;
        }

        var snapshot = lastDeleteSnapshot;
        var generation = lastDeleteResultGeneration;
        var relatedSession = lastDeleteConflictSession;
        var owner = lastDeleteOwner;
        var pending = new HashSet<string>(lastDeletePendingPaths, StringComparer.OrdinalIgnoreCase);
        var restoreViewport = ConflictViewportCaptureRequested?.Invoke(false);
        IsBusy = true;
        try
        {
            var result = await fileActionService.UndoLastDeleteAsync();
            if (result.CompletedPaths.Count > 0)
            {
                IReadOnlyList<string> duplicatePaths = result.CompletedPaths;
                IReadOnlyList<string> conflictPaths = result.CompletedPaths;
                if (owner == "conflict")
                {
                    var disabledByPath = (result.RestoredRecords ?? []).Where(record => record.DisabledMod is not null)
                        .ToDictionary(record => record.Item.OriginalPath, record => record.DisabledMod!, StringComparer.OrdinalIgnoreCase);
                    conflictPaths = result.CompletedPaths.Select(path => disabledByPath.TryGetValue(path, out var disabled)
                        ? disabled.OriginalPath : path).ToArray();
                    if (disabledByPath.Count > 0) await RefreshFileManagementAsync();
                }
                if (owner == "ignored")
                {
                    var records = (result.RestoredRecords ?? []).Where(record => record.DisabledMod is null).ToArray();
                    duplicatePaths = records.Where(record => duplicateHistorySnapshot.SelectMany(group => group.Files).Any(file =>
                        PathRulesEqual(file.Path, record.Item.OriginalPath) && file.Size == record.Size
                            && file.LastWriteTimeUtc == record.LastWriteTimeUtc)).Select(record => record.Item.OriginalPath).ToArray();
                    conflictPaths = records.Where(record => conflictSession.MatchesOriginalFile(record.Item.OriginalPath, record.Size,
                        record.LastWriteTimeUtc)).Select(record => record.Item.OriginalPath).ToArray();
                }
                pending.ExceptWith(result.CompletedPaths);
                if (ReferenceEquals(snapshot, lastDeleteSnapshot)) lastDeletePendingPaths.ExceptWith(result.CompletedPaths);
                if (snapshot is not null && generation == resultGeneration)
                {
                    RestoreDuplicateHistory(duplicatePaths);
                }

                if (ReferenceEquals(relatedSession, conflictSession) && IsConflictComplete)
                {
                    await UpdateConflictFilePathsAsync(conflictSession, conflictPaths, restore: true, restoreViewport);
                }
                var restoredText = result.Failures.Count == 0
                    ? $"已撤回，恢复 {result.CompletedPaths.Count} 个文件"
                    : $"已恢复 {result.CompletedPaths.Count} 个文件，部分项目仍需处理";
                if (owner is "disabled" or "ignored")
                {
                    await RefreshFileManagementAsync();
                    FileManagementFeedback = string.Join("\n", new[] { restoredText, FileManagementFeedback }.Where(text => text.Length > 0));
                }
                else if (owner == "conflict" && ReferenceEquals(relatedSession, conflictSession)) ConflictFileActionStatusText = restoredText;
                else if (owner != "conflict" && generation == resultGeneration) FileActionStatusText = restoredText;
            }

            if (result.Failures.Count > 0)
            {
                fileActionDialogs.ShowProblems("部分文件未能恢复", result.Failures);
            }

            if (!fileActionService.CanUndoLastDelete)
            {
                lastDeleteSnapshot = null;
            }
        }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
        }
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
            foreach (ScanSourceViewModel source in eventArgs.OldItems)
                source.PropertyChanged -= OnSourcePropertyChanged;
        if (eventArgs.NewItems is not null)
            foreach (ScanSourceViewModel source in eventArgs.NewItems)
                source.PropertyChanged += OnSourcePropertyChanged;

        if (ReferenceEquals(sender, Sources))
        {
            if (Sources.Count == 0) ResetResultSession();
            OnPropertyChanged(nameof(Sources));
            StartScanCommand?.RaiseCanExecuteChanged();
        }
        else
        {
            if (ConflictSources.Count == 0) ResetConflictSession();
            OnPropertyChanged(nameof(ConflictSources));
            StartConflictScanCommand?.RaiseCanExecuteChanged();
        }
        RefreshApplicationLocationWarning();
        RefreshLinkTargetActions();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ScanSourceViewModel.IsEnabled))
        {
            StartScanCommand.RaiseCanExecuteChanged();
            StartConflictScanCommand?.RaiseCanExecuteChanged();
            RefreshApplicationLocationWarning();
            RefreshLinkTargetActions();
        }
    }

    private void RefreshAvailability()
    {
        foreach (var source in Sources.Concat(ConflictSources))
        {
            source.RefreshAvailability();
        }

        IsModsRootAvailable = HasModsRoot && Directory.Exists(ModsRoot!);
    }

    private bool TrySaveFolder(string kind, string? path) => TryUpdateSettings(settings =>
        kind == "Mods" ? settings with { ModsRoot = path, Sources = [] } : settings with { TrayRoot = path, Sources = [] });

    private bool TryUpdateSettings(Func<DuplicateSettings, DuplicateSettings> update)
    {
        try
        {
            DuplicateSettings next;
            if (settingsStore is DuplicateSettingsStore disk)
                next = disk.Update(update);
            else
            {
                next = update(new DuplicateSettings(ModsRoot, [], TrayRoot) { HashConcurrency = DuplicateHashConcurrency });
                settingsStore.Save(next);
            }
            ApplySavedSettings(next);
            SettingsFeedback = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsFeedback = "设置未保存，请检查配置文件和目录权限";
            return false;
        }
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathRulesEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private void ReportUnexpectedError(string title, Exception exception)
    {
        IsBusy = false;
        unexpectedErrorHandler.Report(title, exception);
        RefreshResultProperties();
    }

    private void OnResultSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.PropertyName))
        {
            OnPropertyChanged(null);
        }
        else
        {
            OnPropertyChanged(eventArgs.PropertyName);
            if (eventArgs.PropertyName == nameof(HasResults)) OnPropertyChanged(nameof(CanSearchDuplicateResults));
            if (eventArgs.PropertyName == nameof(VisibleDuplicateGroups))
            {
                OnPropertyChanged(nameof(ShowDuplicatePlaceholder));
                OnPropertyChanged(nameof(DuplicatePlaceholderText));
            }
        }

        RefreshResultCommands();
    }

    private void ResetResultSession()
    {
        resultGeneration++;
        scanCancellation?.Cancel();
        if (lastDeleteOwner != "conflict") ClearFileActionHistory();
        FileActionStatusText = string.Empty;
        resultSession.Reset();
        IssueCount = 0;
        DiscoveredFileCount = 0;
        ProgressValue = 0;
        ProgressMaximum = 1;
        IsProgressIndeterminate = false;
        IsStatusVisible = false;
        StatusText = string.Empty;
        StatusDetail = string.Empty;
        DuplicateLinkTarget = null;
    }

    private void ClearFileActionHistory()
    {
        fileActionService.ClearLastDelete();
        lastDeleteSnapshot = null;
        lastDeletePendingPaths.Clear();
        lastDeleteOwner = null;
        lastDeleteConflictSession = null;
        FileActionStatusText = string.Empty;
        OnPropertyChanged(nameof(CanUndoConflictDelete));
        OnPropertyChanged(nameof(CanUndoLastDelete));
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RefreshResultProperties()
    {
        OnPropertyChanged(nameof(CanUndoConflictDelete));
        OnPropertyChanged(nameof(CanUndoLastDelete));
        RefreshResultCommands();
    }

    private void RefreshResultCommands()
    {
        ApplySuggestionsCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ToggleAllGroupsCommand.RaiseCanExecuteChanged();
        ToggleAllConflictGroupsCommand?.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        UndoLastDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        GoHomeCommand.RaiseCanExecuteChanged();
        OpenDuplicateScannerCommand.RaiseCanExecuteChanged();
        OpenConflictCommand.RaiseCanExecuteChanged();
        AddSourceCommand.RaiseCanExecuteChanged();
        ScanLinkTargetCommand.RaiseCanExecuteChanged();
        ChangeModsRootCommand.RaiseCanExecuteChanged();
        RemoveSourceCommand.RaiseCanExecuteChanged();
        StartScanCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        StartConflictScanCommand?.RaiseCanExecuteChanged();
        CancelConflictScanCommand?.RaiseCanExecuteChanged();
        ConflictBackCommand?.RaiseCanExecuteChanged();
        OpenConflictSectionCommand?.RaiseCanExecuteChanged();
        IgnoreConflictCommand?.RaiseCanExecuteChanged();
        OpenConflictRelationshipsCommand?.RaiseCanExecuteChanged();
        JumpToConflictGroupCommand?.RaiseCanExecuteChanged();
        OpenConflictDuplicatesCommand?.RaiseCanExecuteChanged();
        DeleteConflictFileCommand?.RaiseCanExecuteChanged();
        ToggleConflictFileEnabledCommand?.RaiseCanExecuteChanged();
        DeleteSelectedConflictFilesCommand?.RaiseCanExecuteChanged();
        DisableSelectedConflictFilesCommand?.RaiseCanExecuteChanged();
        RefreshSettingsCommands();
        RefreshManagementCommands();
        RefreshResultCommands();
    }
}
