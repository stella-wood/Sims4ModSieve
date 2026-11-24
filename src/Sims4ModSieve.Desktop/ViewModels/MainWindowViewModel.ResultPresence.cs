using System.IO;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<HashSet<string>> RefreshMissingResultMembersAsync(
        IEnumerable<string> selectedPaths, bool fromConflicts)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = conflictSession;
        var generation = resultGeneration;
        var groups = (fromConflicts
                ? session.Cards.Where(card => card.Files.Any(file => selected.Contains(file.Path)))
                    .Select(card => card.Files.Select(file =>
                    (file.Path, Size: file.Stamp?.Length, LastWriteTimeUtc: file.Stamp?.LastWriteTimeUtc, Identity: file.Stamp?.Identity)).ToArray())
                : Groups.Where(group => group.Files.Count > 1 && group.Files.Any(file => selected.Contains(file.Path)))
                    .Select(group => group.Model.Files.Select(file =>
                    (file.Path, Size: (long?)file.Size, LastWriteTimeUtc: (DateTime?)file.LastWriteTimeUtc, file.Identity)).ToArray())).ToArray();
        if (groups.Length == 0) return selected;
        var records = disabledRecords;
        IsBusy = true;
        try
        {
            var missing = await Task.Run(() => groups.SelectMany(group => group).DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Where(file =>
            {
                var disabled = records.FirstOrDefault(record => PathRulesEqual(record.OriginalPath, file.Path)
                    && record.Size == file.Size && record.LastWriteTimeUtc == file.LastWriteTimeUtc
                    && record.Identity is not null && record.Identity == file.Identity);
                return !File.Exists(disabled?.DisabledPath ?? file.Path);
            }).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool IsCurrent() => fromConflicts ? ReferenceEquals(session, conflictSession) && IsConflictComplete
                : generation == resultGeneration;
            if (!IsCurrent()) return [];
            if (missing.Count == 0) return selected;
            var changed = groups.Where(group => group.Any(file => missing.Contains(file.Path))).ToArray();
            fileActionDialogs.ShowResultRefreshNotice(changed.Length);
            if (!IsCurrent()) return [];

            var excluded = changed.SelectMany(group => group.Count(file => !missing.Contains(file.Path)) < 2
                    ? group.Select(file => file.Path) : group.Where(file => missing.Contains(file.Path)).Select(file => file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!fromConflicts) selected.ExceptWith(excluded);
            if (generation == resultGeneration)
            {
                if (duplicateHistoryGeneration == generation) duplicateHistoryMissingPaths.UnionWith(missing);
                resultSession.SynchronizeDeletedPaths(missing);
            }
            if (ReferenceEquals(session, conflictSession) && IsConflictComplete)
            {
                if (!fromConflicts) conflictSelectedPaths.ExceptWith(excluded);
                await UpdateConflictFilePathsAsync(session, missing.ToArray(), restore: false, refreshMissingMembers: true);
            }
            if (fromConflicts && IsCurrent())
            {
                var activePaths = session.Cards.SelectMany(card => card.Files).Select(file => file.Path)
                    .Concat(session.Problems.Select(problem => problem.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                selected.IntersectWith(activePaths);
                conflictSelectedPaths.IntersectWith(activePaths);
                SyncConflictSelectionRows();
            }
            return IsCurrent() ? selected : [];
        }
        finally
        {
            IsBusy = false;
            RefreshResultProperties();
            RefreshConflictSelection();
        }
    }
}
