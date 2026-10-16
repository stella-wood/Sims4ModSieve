using System.Diagnostics;
using System.IO;
using System.Windows;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? lastDeleteOwner;
    private readonly HashSet<string> lastDeletePendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> conflictDetailsCopy;
    private ConflictResultSession? lastDeleteConflictSession;
    private ConflictRelationshipsViewModel? conflictRelationships;
    public ConflictRelationshipsViewModel? ConflictRelationships
    {
        get => conflictRelationships;
        private set => SetProperty(ref conflictRelationships, value);
    }
    public RelayCommand IgnoreConflictCommand { get; private set; } = null!;
    public RelayCommand OpenConflictRelationshipsCommand { get; private set; } = null!;
    public RelayCommand CloseConflictRelationshipsCommand { get; private set; } = null!;
    public RelayCommand JumpToConflictGroupCommand { get; private set; } = null!;
    public RelayCommand OpenConflictDuplicatesCommand { get; private set; } = null!;
    public RelayCommand DeleteConflictFileCommand { get; private set; } = null!;
    public RelayCommand OpenConflictFolderCommand { get; private set; } = null!;
    public RelayCommand CopyConflictDetailsCommand { get; private set; } = null!;
    public Task ConflictFileActionTask { get; private set; } = Task.CompletedTask;
    public event Action<string>? ConflictGroupLocationRequested;
    public event Action<DuplicateGroupViewModel>? DuplicateGroupLocationRequested;
    public bool CanUndoConflictDelete => lastDeleteOwner == "conflict" && fileActionService.CanUndoLastDelete;
    private string conflictFileActionStatusText = string.Empty;
    public string ConflictFileActionStatusText { get => conflictFileActionStatusText; private set => SetProperty(ref conflictFileActionStatusText, value); }

    private void InitializeConflictActions()
    {
        IgnoreConflictCommand = new RelayCommand(value =>
        {
            if (value is not ConflictResultRow row || !IsConflictComplete) return;
            conflictSession.ToggleIgnored(row);
            QueueConflictRowsRefresh();
        }, _ => !IsBusy);
        OpenConflictRelationshipsCommand = new RelayCommand(value =>
        {
            if (value is ConflictResultRow { File: { } file }) ConflictRelationships = conflictSession.Relationships(file.Path);
        }, _ => !IsBusy);
        CloseConflictRelationshipsCommand = new RelayCommand(_ => ConflictRelationships = null);
        JumpToConflictGroupCommand = new RelayCommand(value =>
        {
            if (value is ConflictGroupLink link) ConflictRowsRefreshTask = LocateConflictGroupAsync(link);
        }, _ => !IsBusy);
        OpenConflictDuplicatesCommand = new RelayCommand(value =>
        {
            ConflictRelationships = null;
            ShowScanner();
            var key = value is ConflictGroupLink link ? link.Key : null;
            var duplicate = conflictSession.Cards.FirstOrDefault(card => card.Key == key)?.Duplicate;
            var match = duplicate is null ? null : Groups.FirstOrDefault(group => group.Model.Sha256 == duplicate.Sha256
                && group.Model.FileSize == duplicate.FileSize && group.Model.Files.Any(file => duplicate.Files.Any(other => PathRulesEqual(file.Path, other.Path))));
            if (match is not null) DuplicateGroupLocationRequested?.Invoke(match);
        }, _ => !IsBusy);
        OpenConflictFolderCommand = new RelayCommand(value =>
        {
            var path = value switch { ConflictResultRow row => row.File?.Path, ConflictFile file => file.Path, _ => null };
            if (path is null) return;
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("/select,");
                start.ArgumentList.Add(path);
                Process.Start(start);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
            { fileActionDialogs.ShowProblems("无法打开文件夹", [new(path, exception.Message)]); }
        });
        CopyConflictDetailsCommand = new RelayCommand(value =>
        {
            if (value is not ConflictResultRow row || row.CopyText.Length == 0) return;
            try { conflictDetailsCopy(row.CopyText); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
            { ConflictFileActionStatusText = "复制未完成"; }
        });
        DeleteConflictFileCommand = new RelayCommand(value =>
        {
            if (value is ConflictResultRow row) ConflictFileActionTask = DeleteConflictFileAsync(row);
        }, _ => !IsBusy);
    }

    private async Task LocateConflictGroupAsync(ConflictGroupLink link)
    {
        if (!conflictSession.Cards.Any(card => card.Key == link.Key)) return;
        ConflictRelationships = null;
        conflictView = "all";
        conflictFilter = "all";
        conflictTypes.UnionWith(["override", "collection", "suspected", "duplicates", "scripts"]);
        if (conflictSession.IsGroupIgnored(link.Key)) showIgnoredConflicts = true;
        if (!conflictSession.GetExpandedKeysSnapshot().Contains(link.Key)) conflictSession.ToggleExpanded(link.Key);
        RefreshConflictFilterChoices();
        foreach (var property in new[] { nameof(ConflictView), nameof(ConflictFilter), nameof(IsAllFilterSelected), nameof(IsDifferentFilterSelected),
            nameof(IsSameFilterSelected), nameof(IsIncompleteFilterSelected) }) OnPropertyChanged(property);
        await RefreshConflictRowsAsync();
        ConflictGroupLocationRequested?.Invoke(link.Key);
    }

    public async Task DeleteConflictFileAsync(ConflictResultRow row)
    {
        if (IsBusy || row.File?.Stamp is not { } stamp || !conflictSession.Cards.Any(card => card.Key == row.OwnerKey
            && card.Files.Any(file => PathRulesEqual(file.Path, row.File.Path)))
            && !conflictSession.Problems.Any(problem => PathRulesEqual(problem.Path, row.File.Path))) return;
        var session = conflictSession;
        var duplicateGeneration = resultGeneration;
        var duplicateSnapshot = Groups.Select(group => group.Model).ToArray();
        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() => fileActionService.Preflight(
                [new(row.File.Path, stamp.Length, stamp.LastWriteTimeUtc, row.GroupLabel)], []));
            if (!ReferenceEquals(session, conflictSession)) return;
            if (!preflight.CanExecute) { fileActionDialogs.ShowProblems("暂时不能删除", preflight.Problems); return; }
            if (!fileActionDialogs.ConfirmDeletion(preflight)) return;
            var result = await fileActionService.DeleteAsync(preflight);
            if (result.CompletedPaths.Count > 0)
            {
                if (!ReferenceEquals(session, conflictSession))
                {
                    if (duplicateGeneration == resultGeneration) resultSession.SynchronizeDeletedPaths(result.CompletedPaths);
                    fileActionService.ClearLastDelete();
                    return;
                }
                RememberDeletion("conflict", session, duplicateSnapshot, duplicateGeneration, result.CompletedPaths);
                if (ReferenceEquals(session, conflictSession))
                {
                    // 旧投影继续使用其不可变快照，更新完成后才发布新行。
                    await UpdateConflictFilePathsAsync(session, result.CompletedPaths, restore: false);
                }
                if (duplicateGeneration == resultGeneration) resultSession.SynchronizeDeletedPaths(result.CompletedPaths);
                if (ReferenceEquals(session, conflictSession)) ConflictFileActionStatusText = $"已移入回收站 {result.CompletedPaths.Count} 个文件";
            }
            if (result.Failures.Count > 0) fileActionDialogs.ShowProblems("部分文件未能删除", result.Failures);
        }
        catch (Exception exception) { unexpectedErrorHandler.Report("删除没有完成", exception); }
        finally { IsBusy = false; RefreshResultProperties(); RefreshConflictResults(); OnPropertyChanged(nameof(CanUndoConflictDelete)); }
    }

    private void RememberDeletion(string owner, ConflictResultSession session, IReadOnlyList<DuplicateGroup> snapshot, int generation, IReadOnlyList<string> completedPaths)
    {
        lastDeletePendingPaths.Clear();
        lastDeletePendingPaths.UnionWith(completedPaths);
        lastDeleteOwner = owner;
        lastDeleteConflictSession = session;
        lastDeleteSnapshot = snapshot;
        lastDeleteResultGeneration = generation;
    }

    private void ClearConflictFileActionHistory()
    {
        ConflictFileActionStatusText = string.Empty;
        if (lastDeleteOwner == "conflict") ClearFileActionHistory();
    }

    private async Task UpdateConflictFilePathsAsync(ConflictResultSession session, IReadOnlyList<string> paths, bool restore)
    {
        var prepared = session.ProjectionSnapshot();
        await Task.Run(() =>
        {
            if (restore) prepared.ApplyRestoredPaths(paths);
            else prepared.ApplyDeletedPaths(paths);
        });
        if (!ReferenceEquals(session, conflictSession) || !IsConflictComplete) return;
        session.AcceptFileChanges(prepared);
        await RefreshConflictRowsAsync();
        RefreshConflictResults();
    }
}
