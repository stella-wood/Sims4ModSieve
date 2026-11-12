using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly FileManagementStore fileManagementStore;
    private readonly Func<IReadOnlyList<string>> pickIgnoredFiles;
    private readonly ModDisableService modDisableService;
    private readonly Func<IReadOnlyList<string>> pickDisabledFiles;
    private readonly Func<string, bool> confirmModChange;
    private IReadOnlyList<DisabledModRecord> disabledRecords = [];
    private IReadOnlyList<IgnoredFileRecord> ignoredRecords = [];
    private string fileManagementFeedback = string.Empty;
    private int duplicateHistoryGeneration = -1;
    private IReadOnlyList<DuplicateGroup> duplicateHistorySnapshot = [];
    private readonly HashSet<string> duplicateHistoryMissingPaths = new(StringComparer.OrdinalIgnoreCase);

    public FileManagementSectionViewModel DisabledFiles { get; private set; } = null!;
    public FileManagementSectionViewModel RecentDeletedFiles { get; private set; } = null!;
    public FileManagementSectionViewModel IgnoredFiles { get; private set; } = null!;
    public bool IsFileManagementSettings { get => settingsSection == "files"; set { if (value && !IsBusy) SelectSettingsSection("files"); } }
    public string FileManagementFeedback { get => fileManagementFeedback; private set => SetProperty(ref fileManagementFeedback, value); }
    public AsyncRelayCommand RefreshFileManagementCommand { get; private set; } = null!;
    public Task FileManagementRefreshTask { get; private set; } = Task.CompletedTask;
    private bool isFileManagementRefreshing;
    public bool IsFileManagementRefreshing
    {
        get => isFileManagementRefreshing;
        private set
        {
            if (!SetProperty(ref isFileManagementRefreshing, value)) return;
            OnPropertyChanged(nameof(CanEditFileManagement));
            RefreshManagementCommands();
        }
    }
    public bool CanEditFileManagement => !IsBusy && !IsFileManagementRefreshing;

    private void InitializeFileManagement()
    {
        RefreshFileManagementCommand = new(() => { StartFileManagementRefresh(); return FileManagementRefreshTask; },
            exception => FileManagementFeedback = exception.Message, () => CanEditFileManagement);
        DisabledFiles = new()
        {
            Title = "已禁用", TimeHeader = "禁用时间", EmptyText = "暂无已禁用文件", HasAdd = true, IsDisabled = true,
            AddCommand = ModChangeCommand(() => DisableFilesAsync(pickDisabledFiles())),
        };
        RecentDeletedFiles = new()
        {
            Title = "最近删除", TimeHeader = "删除时间", EmptyText = "暂无最近删除的文件", HasFolder = true,
            OpenFolderCommand = SettingsCommand(() => RunSettingsOperation(settingsSupport.OpenRecycleBin,
                message => FileManagementFeedback = message, "", "无法打开回收站")),
        };
        IgnoredFiles = new()
        {
            Title = "已忽略", TimeHeader = "忽略时间", EmptyText = "暂无已忽略文件", HasAdd = true, IsIgnored = true,
            AddCommand = SettingsCommand(() => AddIgnoredFiles(pickIgnoredFiles())),
        };
        RecentDeletedFiles.BulkRestoreCommand = ManagementCommand(() => RestoreManagedFilesAsync(
            RecentDeletedFiles.Files.Where(row => row.IsSelected).Select(row => row.Id).ToArray()), () => RecentDeletedFiles.HasSelection);
        IgnoredFiles.BulkRemoveCommand = new(_ => RemoveIgnoredFiles(IgnoredFiles.Files.Where(row => row.IsSelected).Select(row => row.Path).ToArray()),
            _ => !IsBusy && IgnoredFiles.HasSelection);
        IgnoredFiles.BulkDeleteCommand = ModChangeCommand(() => DeleteIgnoredFilesAsync(
            IgnoredFiles.Files.Where(row => row.IsSelected).Select(row => row.Path).ToArray()), () => IgnoredFiles.HasSelection);
        DisabledFiles.BulkRestoreCommand = ModChangeCommand(() => RestoreDisabledFilesAsync(
            DisabledFiles.Files.Where(row => row.IsSelected).Select(row => row.Id).ToArray()), () => DisabledFiles.HasSelection);
        DisabledFiles.BulkDeleteCommand = ModChangeCommand(() => DeleteDisabledFilesAsync(
            DisabledFiles.Files.Where(row => row.IsSelected).Select(row => row.Id).ToArray()), () => DisabledFiles.HasSelection);
        try { PublishManagementData(fileManagementStore.Load()); }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "无法读取删除与忽略配置：" + exception.Message; }
        try { PublishDisabledMods(modDisableService.Load()); }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "无法读取禁用记录：" + exception.Message; }
    }

    private AsyncRelayCommand ManagementCommand(Func<Task> action, Func<bool>? canExecute = null) =>
        new(async () =>
        {
            IsBusy = true;
            try { await action(); }
            finally { IsBusy = false; RefreshManagementCommands(); }
        }, exception => FileManagementFeedback = exception.Message, () => !IsBusy && (canExecute?.Invoke() ?? true));

    private static bool IsManagementException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;

    private void PublishManagementData(FileManagementData data)
    {
        ignoredRecords = data.Ignored;
        IgnoredFiles.Replace(data.Ignored.OrderByDescending(record => record.IgnoredAtUtc).Select(record => new ManagedFileRow
        {
            Id = record.Path, Path = record.Path, SizeText = DuplicateGroupViewModel.FormatBytes(record.Size),
            TimeText = ManagementTime(record.IgnoredAtUtc), TimeTooltip = record.IgnoredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            CanRemove = true, RemoveCommand = new(_ => RemoveIgnoredFiles([record.Path]), _ => !IsBusy),
            CanDelete = true, DeleteCommand = ModChangeCommand(() => DeleteIgnoredFileAsync(record.Path)),
        }));
        conflictSession.SetIgnoredFiles(ignoredRecords.Select(record => record.Path));
        if (IsConflictComplete) QueueConflictRowsRefresh();
    }

    private bool ChangeManagementData(Func<FileManagementData, FileManagementData> change)
    {
        try { PublishManagementData(fileManagementStore.Update(change)); FileManagementFeedback = ""; return true; }
        catch (Exception exception) when (IsManagementException(exception))
        { FileManagementFeedback = "未保存：" + exception.Message; return false; }
    }

    public void AddIgnoredFiles(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
        try
        {
            var candidates = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                var info = new FileInfo(path);
                return new IgnoredFileRecord(path, info.Length, DateTime.UtcNow);
            }).ToArray();
            ChangeManagementData(data => data with { Ignored = data.Ignored.Concat(candidates)
                .DistinctBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ToArray() });
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "无法添加文件：" + exception.Message; }
    }

    private void TogglePersistentIgnoredFile(ConflictResultRow row)
    {
        if (row.File is null) { conflictSession.ToggleIgnored(row); QueueConflictRowsRefresh(); return; }
        var path = Path.GetFullPath(row.File.Path);
        if (ignoredRecords.Any(record => StringComparer.OrdinalIgnoreCase.Equals(record.Path, path))) RemoveIgnoredFiles([path]);
        else ChangeManagementData(data => data with { Ignored = data.Ignored.Append(new IgnoredFileRecord(path, row.File.Stamp?.Length ?? 0, DateTime.UtcNow)).ToArray() });
    }

    public void RemoveIgnoredFiles(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
        var remove = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChangeManagementData(data => data with { Ignored = data.Ignored.Where(record => !remove.Contains(record.Path)).ToArray() });
    }

    public Task DeleteIgnoredFileAsync(string path) => DeleteIgnoredFilesAsync([path]);

    public async Task DeleteIgnoredFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = ignoredRecords.Where(record => selected.Contains(record.Path)).ToArray();
        if (records.Length == 0) return;
        var generation = resultGeneration;
        var snapshot = Groups.Select(group => group.Model).ToArray();
        var session = conflictSession;
        var disabled = disabledRecords;
        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() =>
            {
                var candidates = new List<FileDeletionCandidate>();
                var failures = new List<FileActionFailure>();
                foreach (var record in records)
                {
                    try
                    {
                        var info = new FileInfo(record.Path);
                        var disabledRecord = disabled.SingleOrDefault(item => PathRulesEqual(item.DisabledPath, record.Path));
                        candidates.Add(new(record.Path, disabledRecord?.Size ?? info.Length,
                            disabledRecord?.LastWriteTimeUtc ?? info.LastWriteTimeUtc, "", disabledRecord,
                            disabledRecord is null ? PhysicalFileIdentity.Read(record.Path) : disabledRecord.Identity));
                    }
                    catch (Exception exception) when (IsManagementException(exception))
                    { failures.Add(new(record.Path, exception.Message)); }
                }
                var checkedFiles = fileActionService.Preflight(candidates, []);
                return checkedFiles with { Problems = checkedFiles.Problems.Concat(failures).ToArray() };
            });
            if (!preflight.CanExecute) { fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems); return; }
            if (!fileActionDialogs.ConfirmDeletion(preflight)) return;
            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                foreach (var row in IgnoredFiles.Files.Where(row => result.CompletedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase)))
                    row.IsSelected = false;
                if (generation == resultGeneration)
                {
                    RememberDeletion("ignored", session, snapshot, generation, result.CompletedPaths);
                    resultSession.SynchronizeDeletedPaths(result.CompletedPaths);
                }
                else fileActionService.ClearLastDelete();
                if (ReferenceEquals(session, conflictSession) && IsConflictComplete)
                    await UpdateConflictFilePathsAsync(session, result.CompletedPaths, restore: false);
                await RefreshFileManagementAsync();
                FileManagementFeedback = string.Join("\n", new[] { $"已移入回收站 {result.CompletedPaths.Count} 个文件", FileManagementFeedback }
                    .Where(text => text.Length > 0));
            }
            if (result.Failures.Count > 0) fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "删除未完成：" + exception.Message; }
        finally { IsBusy = false; RefreshResultProperties(); RefreshManagementCommands(); }
    }

    private static string ManagementTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? "今天 " + local.ToString("HH:mm") : local.Date == DateTime.Today.AddDays(-1)
            ? "昨天 " + local.ToString("HH:mm") : local.ToString("yyyy-MM-dd HH:mm");
    }

    private static IReadOnlyList<string> PickIgnoredFiles()
    {
        var dialog = new OpenFileDialog { Title = "添加已忽略文件", Multiselect = true,
            Filter = "Mod 文件|*.package;*.ts4script|所有文件|*.*", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    private static IReadOnlyList<string> PickDisabledFiles()
    {
        var dialog = new OpenFileDialog { Title = "选择要禁用的文件", Multiselect = true,
            Filter = "Mod 文件|*.package;*.ts4script", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    private static bool ConfirmModChange(string action) => MessageBox.Show(Application.Current.MainWindow,
        $"请先关闭游戏\n确定{action}所选文件？", "", MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private AsyncRelayCommand ModChangeCommand(Func<Task> action, Func<bool>? canExecute = null) =>
        new(action, exception => FileManagementFeedback = exception.Message,
            () => !IsBusy && (canExecute?.Invoke() ?? true));

    private void PublishDisabledMods(DisabledModsResult result)
    {
        disabledRecords = result.Records;
        DisabledFiles.Replace(result.Records.OrderByDescending(record => record.DisabledAtUtc).Select(record => new ManagedFileRow
        {
            Id = record.Id, Path = record.OriginalPath, SizeText = DuplicateGroupViewModel.FormatBytes(record.Size),
            TimeText = ManagementTime(record.DisabledAtUtc), TimeTooltip = record.DisabledAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            CanRestore = true, RestoreText = "启用", RestoreCommand = ModChangeCommand(() => RestoreDisabledFilesAsync([record.Id])),
            CanDelete = true, DeleteCommand = ModChangeCommand(() => DeleteDisabledFileAsync(record.Id)),
        }));
        if (result.Failures.Count > 0) FileManagementFeedback = string.Join("\n", result.Failures.Select(failure => failure.Message).Distinct());
        if (IsConflictComplete) QueueConflictRowsRefresh();
    }

    public async Task DisableFilesAsync(IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, FileStamp>? expectedStamps = null, bool confirmed = false)
    {
        if (IsBusy || paths.Count == 0 || (!confirmed && !confirmModChange("禁用"))) return;
        var generation = resultGeneration;
        var snapshot = Groups.Select(group => group.Model).ToArray();
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => modDisableService.Disable(paths, expectedStamps));
            if (result.CompletedPaths.Count > 0)
            {
                if (generation == resultGeneration)
                {
                    TrackDuplicateHistory(snapshot, generation, result.CompletedPaths);
                    resultSession.SynchronizeDeletedPaths(result.CompletedPaths);
                }
            }
            await FinishModChangeAsync(result, "禁用", restore: false);
            if (IsConflictComplete) await RefreshConflictRowsAsync();
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "禁用未完成：" + exception.Message; }
        finally { IsBusy = false; RefreshResultProperties(); RefreshManagementCommands(); }
    }

    public Task DeleteDisabledFileAsync(string id) => DeleteDisabledFilesAsync([id]);

    public async Task DeleteDisabledFilesAsync(IReadOnlyList<string> ids)
    {
        if (IsBusy || ids.Count == 0) return;
        var records = disabledRecords.Where(record => ids.Contains(record.Id)).ToArray();
        if (records.Length == 0) return;
        var session = conflictSession;
        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() => fileActionService.Preflight(
                records.Select(record => new FileDeletionCandidate(record.DisabledPath, record.Size,
                    record.LastWriteTimeUtc, "", record, record.Identity)).ToArray(), []));
            if (!preflight.CanExecute) { fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems); return; }
            if (!fileActionDialogs.ConfirmDeletion(preflight)) return;
            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                lastDeleteOwner = "disabled";
                lastDeleteSnapshot = null;
                lastDeleteConflictSession = null;
                lastDeleteResultGeneration = resultGeneration;
                lastDeletePendingPaths.Clear();
                lastDeletePendingPaths.UnionWith(result.CompletedPaths);
                PublishDisabledMods(new(disabledRecords.Where(record => !result.CompletedPaths.Contains(
                    record.DisabledPath, StringComparer.OrdinalIgnoreCase)).ToArray(), []));
                if (ReferenceEquals(session, conflictSession) && IsConflictComplete)
                    await UpdateConflictFilePathsAsync(session, records.Where(record => result.CompletedPaths.Contains(record.DisabledPath, StringComparer.OrdinalIgnoreCase))
                        .Select(record => record.OriginalPath).ToArray(), restore: false);
                await RefreshFileManagementAsync();
                FileManagementFeedback = string.Join("\n", new[] { "已移入回收站", FileManagementFeedback }.Where(text => text.Length > 0));
            }
            if (result.Failures.Count > 0) fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "删除未完成：" + exception.Message; }
        finally { IsBusy = false; RefreshResultProperties(); RefreshManagementCommands(); }
    }

    public async Task RestoreDisabledFilesAsync(IReadOnlyList<string> ids)
    {
        if (IsBusy || ids.Count == 0 || !confirmModChange("启用")) return;
        var records = disabledRecords.Where(record => ids.Contains(record.Id)).ToArray();
        var generation = resultGeneration;
        var session = conflictSession;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => modDisableService.Restore(ids));
            var restored = records.Where(record => result.CompletedPaths.Contains(record.OriginalPath, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (generation == resultGeneration)
                RestoreDuplicateHistory(restored.Where(record => duplicateHistorySnapshot.SelectMany(group => group.Files).Any(file =>
                    PathRulesEqual(file.Path, record.OriginalPath) && file.Size == record.Size && file.LastWriteTimeUtc == record.LastWriteTimeUtc))
                    .Select(record => record.OriginalPath).ToArray());
            if (ReferenceEquals(session, conflictSession) && IsConflictComplete)
                await UpdateConflictFilePathsAsync(session, restored.Where(record => session.MatchesOriginalFile(
                    record.OriginalPath, record.Size, record.LastWriteTimeUtc)).Select(record => record.OriginalPath).ToArray(), restore: true);
            await FinishModChangeAsync(result, "启用", restore: true);
            if (IsConflictComplete) await RefreshConflictRowsAsync();
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "启用未完成：" + exception.Message; }
        finally { IsBusy = false; RefreshResultProperties(); RefreshManagementCommands(); }
    }

    private async Task FinishModChangeAsync(ModDisableBatchResult result, string action, bool restore)
    {
        var failures = result.Failures.ToList();
        if (restore)
            PublishDisabledMods(new(disabledRecords.Where(record => !result.CompletedPaths.Contains(
                record.OriginalPath, StringComparer.OrdinalIgnoreCase)).ToArray(), []));
        try
        {
            var current = await Task.Run(modDisableService.Load);
            PublishDisabledMods(current);
            failures.AddRange(current.Failures);
        }
        catch (Exception exception) when (IsManagementException(exception))
        { failures.Add(new("", "无法刷新禁用记录：" + exception.Message)); }
        var completed = result.CompletedPaths.Count > 0
            ? $"已{action} {result.CompletedPaths.Count} 个文件，可删除 localthumbcache.package" : "";
        FileManagementFeedback = string.Join("\n", new[] { completed }.Concat(failures.Select(failure => failure.Message))
            .Where(message => message.Length > 0).Distinct());
        if (failures.Count > 0) fileActionDialogs.ShowProblems($"{action}中有项目需要处理", failures);
    }

    public async Task RefreshFileManagementAsync()
    {
        FileManagementFeedback = "";
        try
        {
            PublishDisabledMods(await Task.Run(modDisableService.Load));
            if (IsConflictComplete)
                await UpdateConflictFilePathsAsync(conflictSession, disabledRecords.Where(record => conflictSession.MatchesOriginalFile(
                    record.OriginalPath, record.Size, record.LastWriteTimeUtc)).Select(record => record.OriginalPath).ToArray(), restore: true);
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "无法刷新禁用记录：" + exception.Message; }
        try
        {
            PublishManagementData(fileManagementStore.Load());
            var recent = await fileActionService.GetRecentlyDeletedAsync();
            RecentDeletedFiles.Replace(recent.Files.Select(record => new ManagedFileRow
            {
                Id = record.Id, Path = record.Item.OriginalPath, SizeText = DuplicateGroupViewModel.FormatBytes(record.Size),
                TimeText = ManagementTime(record.DeletedAtUtc), TimeTooltip = record.DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                CanRestore = true, RestoreCommand = ManagementCommand(() => RestoreManagedFilesAsync([record.Id])),
            }));
            if (recent.Failures.Count > 0) FileManagementFeedback = string.Join("\n", recent.Failures.Select(failure => failure.Message).Distinct());
            RefreshResultProperties();
        }
        catch (Exception exception) when (IsManagementException(exception)) { FileManagementFeedback = "无法刷新：" + exception.Message; }
    }

    private void StartFileManagementRefresh()
    {
        if (IsFileManagementRefreshing) return;
        FileManagementRefreshTask = RunFileManagementRefreshAsync();
    }

    private async Task RunFileManagementRefreshAsync()
    {
        IsFileManagementRefreshing = true;
        try { await RefreshFileManagementAsync(); }
        finally { IsFileManagementRefreshing = false; }
    }

    public async Task RestoreManagedFilesAsync(IReadOnlyList<string> ids)
    {
        var result = await fileActionService.RestoreDeletedAsync(ids);
        if (result.CompletedPaths.Count > 0)
        {
            var records = (result.RestoredRecords ?? []).Where(record => record.DisabledMod is null).ToArray();
            var duplicatePaths = records.Where(record => duplicateHistorySnapshot.SelectMany(group => group.Files).Any(file =>
                StringComparer.OrdinalIgnoreCase.Equals(file.Path, record.Item.OriginalPath) && file.Size == record.Size
                    && file.LastWriteTimeUtc == record.LastWriteTimeUtc)).Select(record => record.Item.OriginalPath).ToArray();
            RestoreDuplicateHistory(duplicatePaths);
            lastDeletePendingPaths.ExceptWith(result.RestoredUndoPaths ?? []);
            var conflictPaths = records.Where(record => conflictSession.MatchesOriginalFile(record.Item.OriginalPath, record.Size,
                record.LastWriteTimeUtc)).Select(record => record.Item.OriginalPath).ToArray();
            if (IsConflictComplete && conflictPaths.Length > 0) await UpdateConflictFilePathsAsync(conflictSession, conflictPaths, restore: true);
        }
        if (result.Failures.Count > 0) fileActionDialogs.ShowProblems("部分文件未能恢复", result.Failures);
        await RefreshFileManagementAsync();
    }

    private void TrackDuplicateHistory(IReadOnlyList<DuplicateGroup> snapshot, int generation, IReadOnlyList<string> paths)
    {
        if (duplicateHistoryGeneration != generation)
        {
            duplicateHistoryGeneration = generation;
            duplicateHistorySnapshot = snapshot;
            duplicateHistoryMissingPaths.Clear();
        }
        duplicateHistoryMissingPaths.UnionWith(paths);
    }

    private void RestoreDuplicateHistory(IReadOnlyList<string> paths)
    {
        if (duplicateHistoryGeneration != resultGeneration || !paths.Any(duplicateHistoryMissingPaths.Contains)) return;
        duplicateHistoryMissingPaths.ExceptWith(paths);
        resultSession.RestoreCompletedPaths(duplicateHistorySnapshot, duplicateHistoryMissingPaths);
    }

    private void RefreshManagementCommands()
    {
        if (RecentDeletedFiles is null) return;
        RefreshFileManagementCommand.RaiseCanExecuteChanged();
        foreach (var section in new[] { DisabledFiles, RecentDeletedFiles, IgnoredFiles })
        {
            section.BulkRestoreCommand?.RaiseCanExecuteChanged();
            section.BulkDeleteCommand?.RaiseCanExecuteChanged();
            section.BulkRemoveCommand?.RaiseCanExecuteChanged();
            (section.AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (section.AddCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            section.OpenFolderCommand?.RaiseCanExecuteChanged();
            foreach (var row in section.Files)
            {
                row.RestoreCommand?.RaiseCanExecuteChanged(); row.RemoveCommand?.RaiseCanExecuteChanged(); row.DeleteCommand?.RaiseCanExecuteChanged();
            }
        }
    }
}
