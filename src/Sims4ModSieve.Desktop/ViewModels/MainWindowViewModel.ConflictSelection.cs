using Sims4ModSieve.Core.Settings;
using System.IO;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> conflictSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool isConflictMultiSelect;

    public AsyncRelayCommand DeleteSelectedConflictFilesCommand { get; private set; } = null!;
    public AsyncRelayCommand DisableSelectedConflictFilesCommand { get; private set; } = null!;

    public bool IsConflictMultiSelect
    {
        get => isConflictMultiSelect;
        set
        {
            if (!SetProperty(ref isConflictMultiSelect, value)) return;
            if (!value)
            {
                ShowSelectedConflicts = false;
                ClearConflictSelection();
            }
            OnPropertyChanged(nameof(ConflictMultiSelectText));
            RefreshConflictSelection();
        }
    }

    public int ConflictSelectedCount => conflictSelectedPaths.Count;
    public bool HasConflictSelection => conflictSelectedPaths.Count > 0;
    public string ConflictSelectionText => $"已勾选 {ConflictSelectedCount} 份文件";
    public string ConflictMultiSelectText => IsConflictMultiSelect ? "取消多选" : "多选";
    public bool CanRunConflictSelectionAction => IsConflictMultiSelect && HasConflictSelection && !IsBusy && IsConflictComplete;

    // 行上的操作只在那一行已勾选时扩展到全部勾选；未勾选的行只作用于自己。
    public bool ConflictActionAppliesToSelection(ConflictResultRow row) =>
        IsConflictMultiSelect && row.File is { } file && conflictSelectedPaths.Contains(file.Path);

    private void InitializeConflictSelection()
    {
        DeleteSelectedConflictFilesCommand = new AsyncRelayCommand(DeleteSelectedConflictFilesAsync,
            exception => ReportUnexpectedError("删除没有完成", exception), () => CanRunConflictSelectionAction);
        DisableSelectedConflictFilesCommand = new AsyncRelayCommand(DisableSelectedConflictFilesAsync,
            exception => ReportUnexpectedError("禁用没有完成", exception), () => CanRunConflictSelectionAction);
    }

    public void SetConflictFileSelected(ConflictResultRow row, bool selected)
    {
        if (!IsConflictMultiSelect || row.File is not { Stamp: not null }) return;
        SetRowSelected(row, selected);
        SyncConflictSelectionRows();
        RefreshConflictSelection();
    }

    // 组复选框按筛选和副本折叠后的文件计算，不受展开状态影响。
    public void ToggleConflictGroupSelection(ConflictResultRow header)
    {
        if (!IsConflictMultiSelect) return;
        var files = header.SelectionFiles;
        if (files.Count == 0) return;
        var select = !files.All(file => conflictSelectedPaths.Contains(file.Path));
        foreach (var file in files)
            if (select) conflictSelectedPaths.Add(file.Path); else conflictSelectedPaths.Remove(file.Path);
        SyncConflictSelectionRows();
        RefreshConflictSelection();
    }

    public void ClearConflictSelection()
    {
        conflictSelectedPaths.Clear();
        SyncConflictSelectionRows();
        RefreshConflictSelection();
    }

    private void RefreshConflictSelection()
    {
        OnPropertyChanged(nameof(ConflictSelectedCount));
        OnPropertyChanged(nameof(HasConflictSelection));
        OnPropertyChanged(nameof(ConflictSelectionText));
        OnPropertyChanged(nameof(CanRunConflictSelectionAction));
        DeleteSelectedConflictFilesCommand?.RaiseCanExecuteChanged();
        DisableSelectedConflictFilesCommand?.RaiseCanExecuteChanged();
        if (showSelectedConflicts && IsConflictComplete) QueueConflictRowsRefresh();
    }

    private bool? ConflictGroupSelection(ConflictResultRow header)
    {
        var selected = header.SelectionFiles.Count(file => conflictSelectedPaths.Contains(file.Path));
        return selected == 0 ? false : selected == header.SelectionFiles.Count ? true : null;
    }

    private bool IsRowSelected(ConflictResultRow row) => row.File is { } file && conflictSelectedPaths.Contains(file.Path);

    private void SetRowSelected(ConflictResultRow row, bool selected)
    {
        if (row.File is not { } file) return;
        if (selected) conflictSelectedPaths.Add(file.Path); else conflictSelectedPaths.Remove(file.Path);
        row.Selection.IsSelected = selected;
    }

    private void SyncConflictSelectionRows()
    {
        foreach (var row in conflictRows.Where(row => row.Kind == ConflictRowKind.File))
            row.Selection.IsSelected = row.IsSelectableFile && IsRowSelected(row);
        foreach (var header in conflictRows.Where(row => row.IsCardStart))
            header.Selection.IsSelected = ConflictGroupSelection(header);
    }

    // 已勾选的文件按会话里的卡片解析；被筛选隐藏的文件仍参与操作，数量提示保持同一口径。
    private List<FileDeletionCandidate> ConflictSelectionCandidates()
    {
        var candidates = new List<FileDeletionCandidate>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in conflictSession.Cards)
            foreach (var file in card.Files)
            {
                if (!conflictSelectedPaths.Contains(file.Path) || file.Stamp is not { } stamp) continue;
                var alreadySelected = indexes.TryGetValue(file.Path, out var index);
                if (alreadySelected && card.Duplicate is null) continue;
                var candidate = new FileDeletionCandidate(file.Path, stamp.Length, stamp.LastWriteTimeUtc, $"第 {card.GroupIndex} 组",
                    ExpectedIdentity: stamp.Identity, ExpectedSha256: card.Duplicate?.Sha256);
                // 重复卡片的文件戳和整文件哈希描述同一次读取，优先一起使用。
                if (alreadySelected) candidates[index] = candidate;
                else
                {
                    indexes.Add(file.Path, candidates.Count);
                    candidates.Add(candidate);
                }
            }
        foreach (var problem in conflictSession.Problems)
            if (conflictSelectedPaths.Contains(problem.Path) && problem.Stamp is { } stamp && indexes.TryAdd(problem.Path, candidates.Count))
                candidates.Add(new(problem.Path, stamp.Length, stamp.LastWriteTimeUtc, "", ExpectedIdentity: stamp.Identity));
        return candidates;
    }

    private static string ConflictConfirmation(string action, int count, bool closeGame) =>
        MultiFileConfirmationWindow.ConfirmationMessage(action, count, closeGame);

    private async Task DeleteSelectedConflictFilesAsync()
    {
        if (IsBusy || !IsConflictComplete || !IsConflictMultiSelect) return;
        var selected = ConflictSelectionCandidates();
        var originalRows = conflictRows;
        var restoreViewport = ConflictViewportCaptureRequested?.Invoke(false);
        var remaining = await RefreshMissingResultMembersAsync(selected.Select(candidate => candidate.Path), fromConflicts: true);
        var candidates = selected.Where(candidate => remaining.Contains(candidate.Path)).Select(candidate =>
        {
            var disabled = disabledRecords.FirstOrDefault(record => PathRulesEqual(record.OriginalPath, candidate.Path)
                && record.Size == candidate.ExpectedSize && record.LastWriteTimeUtc == candidate.ExpectedLastWriteTimeUtc);
            return disabled is null ? candidate : candidate with
            {
                Path = disabled.DisabledPath, DisabledMod = disabled, ExpectedIdentity = disabled.Identity,
            };
        }).ToArray();
        if (candidates.Length == 0) return;
        var session = conflictSession;
        var duplicateGeneration = resultGeneration;
        var duplicateSnapshot = Groups.Select(group => group.Model).ToArray();
        if (!ReferenceEquals(conflictRows, originalRows)) restoreViewport = ConflictViewportCaptureRequested?.Invoke(false);
        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() => fileActionService.Preflight(candidates, []));
            if (!ReferenceEquals(session, conflictSession)) return;
            if (!preflight.CanExecute) { fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems); return; }
            if (!fileActionDialogs.ConfirmFiles("删除文件",
                ConflictConfirmation("删除", preflight.Candidates.Count, closeGame: true))) return;
            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                var byPath = candidates.ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
                var completedPaths = result.CompletedPaths.Select(path => byPath[path].DisabledMod?.OriginalPath ?? path).ToArray();
                PublishDisabledMods(new(disabledRecords.Where(record => !result.CompletedPaths.Contains(
                    record.DisabledPath, StringComparer.OrdinalIgnoreCase)).ToArray(), []));
                if (!ReferenceEquals(session, conflictSession))
                {
                    if (duplicateGeneration == resultGeneration) resultSession.SynchronizeDeletedPaths(completedPaths);
                    fileActionService.ClearLastDelete();
                    return;
                }
                RememberDeletion("conflict", session, duplicateSnapshot, duplicateGeneration, result.CompletedPaths);
                await UpdateConflictFilePathsAsync(session, completedPaths, restore: false, restoreViewport);
                if (duplicateGeneration == resultGeneration) resultSession.SynchronizeDeletedPaths(completedPaths);
                if (ReferenceEquals(session, conflictSession)) ConflictFileActionStatusText = $"本次删除 {result.CompletedPaths.Count} 个文件，已移入回收站";
            }
            if (result.Failures.Count > 0) fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
        }
        catch (Exception exception) { unexpectedErrorHandler.Report("删除没有完成", exception); }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
            RefreshConflictResults();
            OnPropertyChanged(nameof(CanUndoConflictDelete));
            RefreshConflictSelection();
        }
    }

    private async Task DisableSelectedConflictFilesAsync()
    {
        if (IsBusy || !IsConflictComplete || !IsConflictMultiSelect) return;
        var selected = ConflictSelectionCandidates();
        var remaining = await RefreshMissingResultMembersAsync(selected.Select(candidate => candidate.Path), fromConflicts: true);
        if (remaining.Count == 0) return;
        var candidates = selected.Where(candidate => remaining.Contains(candidate.Path))
            .Where(candidate => !disabledRecords.Any(record => PathRulesEqual(record.OriginalPath, candidate.Path)
                && record.Size == candidate.ExpectedSize && record.LastWriteTimeUtc == candidate.ExpectedLastWriteTimeUtc))
            .ToArray();
        if (candidates.Length == 0)
        {
            ConflictFileActionStatusText = "所选文件都已禁用";
            return;
        }
        if (!fileActionDialogs.ConfirmFiles("禁用文件", ConflictConfirmation("禁用", candidates.Length, closeGame: true))) return;
        var before = disabledRecords.Count;
        var stamps = candidates.ToDictionary(candidate => candidate.Path,
            candidate => new FileStamp(candidate.ExpectedSize, candidate.ExpectedLastWriteTimeUtc, candidate.ExpectedIdentity),
            StringComparer.OrdinalIgnoreCase);
        await DisableFilesAsync(candidates.Select(candidate => candidate.Path).ToArray(), stamps, confirmed: true);
        ConflictFileActionStatusText = FileManagementFeedback;
        if (disabledRecords.Count > before) ClearConflictSelection();
    }

    private void IgnoreSelectedConflictFiles()
    {
        if (IsBusy || !IsConflictComplete || !IsConflictMultiSelect || conflictSelectedPaths.Count == 0) return;
        var candidates = ConflictSelectionCandidates();
        if (candidates.Count == 0) return;
        var allIgnored = candidates.All(candidate => ignoredRecords.Any(record =>
            StringComparer.OrdinalIgnoreCase.Equals(record.Path, Path.GetFullPath(candidate.Path))));
        var action = allIgnored ? "取消忽略" : "忽略";
        if (candidates.Count > 1 && !fileActionDialogs.ConfirmFiles("忽略文件",
            ConflictConfirmation(action, candidates.Count, closeGame: false))) return;
        var paths = candidates.Select(candidate => candidate.Path).ToArray();
        if (allIgnored)
        {
            var remove = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!ChangeManagementData(data => data with
                {
                    Ignored = data.Ignored.Where(record => !remove.Contains(record.Path)).ToArray(),
                })) return;
        }
        else
        {
            var added = candidates.Select(candidate => new IgnoredFileRecord(
                Path.GetFullPath(candidate.Path), candidate.ExpectedSize, DateTime.UtcNow)).ToArray();
            if (!ChangeManagementData(data => data with
                {
                    Ignored = data.Ignored.Concat(added).DistinctBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                }, animateRemoval: true)) return;
        }
        ConflictFileActionStatusText = $"已{action} {paths.Length} 个文件";
        ClearConflictSelection();
        if (!allIgnored && showSelectedConflicts) QueueConflictRowsRefresh(animateRemoval: true);
    }
}
