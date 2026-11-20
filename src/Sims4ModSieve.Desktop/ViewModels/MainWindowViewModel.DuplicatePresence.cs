using System.IO;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<HashSet<string>> RefreshMissingDuplicateMembersAsync(
        IEnumerable<string> selectedPaths, bool fromConflicts)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = conflictSession;
        var generation = resultGeneration;
        var groups = (fromConflicts
                ? session.Cards.Where(card => card.Duplicate is not null).Select(card => card.Duplicate!)
                : Groups.Select(group => group.Model))
            .Where(group => group.Files.Count > 1 && group.Files.Any(file => selected.Contains(file.Path))).ToArray();
        if (groups.Length == 0) return selected;
        var records = disabledRecords;
        IsBusy = true;
        try
        {
            var missing = await Task.Run(() => groups.SelectMany(group => group.Files).Where(file =>
            {
                var disabled = records.FirstOrDefault(record => PathRulesEqual(record.OriginalPath, file.Path)
                    && record.Size == file.Size && record.LastWriteTimeUtc == file.LastWriteTimeUtc);
                return !File.Exists(disabled?.DisabledPath ?? file.Path);
            }).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool IsCurrent() => fromConflicts ? ReferenceEquals(session, conflictSession) && IsConflictComplete
                : generation == resultGeneration;
            if (!IsCurrent()) return [];
            if (missing.Count == 0) return selected;
            var changed = groups.Where(group => group.Files.Any(file => missing.Contains(file.Path))).ToArray();
            if (!fileActionDialogs.ConfirmDuplicateGroupRefresh(changed.Length) || !IsCurrent()) return [];

            var excluded = changed.SelectMany(group => group.Files.Count(file => !missing.Contains(file.Path)) < 2
                    ? group.Files.Select(file => file.Path) : group.Files.Where(file => missing.Contains(file.Path)).Select(file => file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected.ExceptWith(excluded);
            if (generation == resultGeneration)
            {
                if (duplicateHistoryGeneration == generation) duplicateHistoryMissingPaths.UnionWith(missing);
                resultSession.SynchronizeDeletedPaths(missing);
            }
            if (ReferenceEquals(session, conflictSession) && IsConflictComplete)
            {
                conflictSelectedPaths.ExceptWith(excluded);
                await UpdateConflictFilePathsAsync(session, missing.ToArray(), restore: false, refreshMissingDuplicates: true);
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
