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
            if (!value) ClearConflictSelection();
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

    // 组复选框只作用于当前看得见的文件行；部分勾选时再点一次即全选。
    public void ToggleConflictGroupSelection(ConflictResultRow header)
    {
        if (!IsConflictMultiSelect) return;
        var files = SelectableFiles(header.Key);
        if (files.Length == 0) return;
        var select = !files.All(IsRowSelected);
        foreach (var row in files) SetRowSelected(row, select);
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
    }

    private ConflictResultRow[] SelectableFiles(string ownerKey) => conflictRows
        .Where(row => row.Kind == ConflictRowKind.File && row.IsSelectableFile
            && string.Equals(row.OwnerKey, ownerKey, StringComparison.Ordinal)).ToArray();

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
        var filesByOwner = conflictRows.Where(row => row.IsSelectableFile)
            .ToLookup(row => row.OwnerKey, StringComparer.Ordinal);
        foreach (var header in conflictRows.Where(row => row.IsCardStart))
        {
            var files = filesByOwner[header.Key];
            header.Selection.IsSelected = !files.Any() ? false
                : files.All(IsRowSelected) ? true
                : files.Any(IsRowSelected) ? null : false;
        }
    }

    // 已勾选的文件按会话里的卡片解析；被筛选隐藏的文件仍参与操作，数量提示保持同一口径。
    private List<FileDeletionCandidate> ConflictSelectionCandidates()
    {
        var candidates = new List<FileDeletionCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in conflictSession.Cards)
            foreach (var file in card.Files)
            {
                if (!conflictSelectedPaths.Contains(file.Path) || file.Stamp is not { } stamp || !seen.Add(file.Path)) continue;
                candidates.Add(new(file.Path, stamp.Length, stamp.LastWriteTimeUtc, $"第 {card.GroupIndex} 组",
                    ExpectedIdentity: stamp.Identity, ExpectedSha256: card.Duplicate?.Sha256));
            }
        foreach (var problem in conflictSession.Problems)
            if (conflictSelectedPaths.Contains(problem.Path) && problem.Stamp is { } stamp && seen.Add(problem.Path))
                candidates.Add(new(problem.Path, stamp.Length, stamp.LastWriteTimeUtc, "", ExpectedIdentity: stamp.Identity));
        return candidates;
    }

    private static string ConflictConfirmation(string action, int count, bool closeGame) =>
        MultiFileConfirmationWindow.ConfirmationMessage(action, count, closeGame);

    private async Task DeleteSelectedConflictFilesAsync()
    {
        if (IsBusy || !IsConflictComplete || !IsConflictMultiSelect) return;
        var candidates = ConflictSelectionCandidates().Select(candidate =>
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
        var restoreViewport = ConflictViewportCaptureRequested?.Invoke(false);
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
        var candidates = ConflictSelectionCandidates()
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
                })) return;
        }
        ConflictFileActionStatusText = $"已{action} {paths.Length} 个文件";
        ClearConflictSelection();
    }
}
