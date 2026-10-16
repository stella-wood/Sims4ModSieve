using System.ComponentModel;
using System.IO;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed class DuplicateResultSession : ObservableObject
{
    private readonly DuplicateSelectionService selectionService = new();
    private IReadOnlyList<DuplicateGroupViewModel> groups = Array.Empty<DuplicateGroupViewModel>();
    private bool isBulkUpdatingSelection;
    private bool isBulkUpdatingExpansion;

    public IReadOnlyList<DuplicateGroupViewModel> Groups
    {
        get => groups;
        private set => SetProperty(ref groups, value);
    }

    public bool HasResults => Groups.Count > 0;

    public bool HasNoResults => !HasResults;

    public int DuplicateGroupCount => Groups.Count;

    public int DuplicateFileCount => Groups.Sum(group => group.Files.Count);

    public int SelectedFileCount => Groups.Sum(group => group.SelectedCount);

    public string DuplicateSizeText => DuplicateGroupViewModel.FormatBytes(
        Groups.Sum(group => checked(group.Model.FileSize * group.Files.Count)));

    public string SelectedSizeText => DuplicateGroupViewModel.FormatBytes(
        Groups.Sum(group => group.SelectedBytes));

    public bool ShowExpandAllIcon => Groups.Count == 0 || !Groups.All(group => group.IsExpanded);

    public bool ShowCollapseAllIcon => Groups.Count > 0 && Groups.All(group => group.IsExpanded);

    public string ExpandCollapseToolTip => Groups.Count > 0 && Groups.All(group => group.IsExpanded)
        ? "收起全部"
        : "展开全部";

    public IReadOnlyList<DuplicateGroupViewModel> PrepareGroups(
        IReadOnlyList<DuplicateGroup> reportGroups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportGroups);

        var result = new DuplicateGroupViewModel[reportGroups.Count];
        for (var index = 0; index < reportGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index] = new DuplicateGroupViewModel(reportGroups[index], index + 1);
        }

        return result;
    }

    public void Replace(IReadOnlyList<DuplicateGroupViewModel> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var group in Groups)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
            foreach (var file in group.Files)
            {
                file.PropertyChanged -= OnFilePropertyChanged;
            }
        }

        foreach (var group in value)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
            foreach (var file in group.Files)
            {
                file.PropertyChanged += OnFilePropertyChanged;
            }
        }

        Groups = value;
        RefreshAllProperties();
    }

    public void Reset() => Replace(Array.Empty<DuplicateGroupViewModel>());

    public void ApplySuggestions()
    {
        isBulkUpdatingSelection = true;
        try
        {
            foreach (var group in Groups)
            {
                group.ApplySuggestion();
            }
        }
        finally
        {
            isBulkUpdatingSelection = false;
        }

        RefreshSelectionProperties();
    }

    public void ClearSelection()
    {
        isBulkUpdatingSelection = true;
        try
        {
            foreach (var group in Groups)
            {
                group.ClearSelection();
            }
        }
        finally
        {
            isBulkUpdatingSelection = false;
        }

        RefreshSelectionProperties();
    }

    public void ToggleAllGroups()
    {
        var expand = !Groups.All(group => group.IsExpanded);
        isBulkUpdatingExpansion = true;
        try
        {
            foreach (var group in Groups)
            {
                group.IsExpanded = expand;
            }
        }
        finally
        {
            isBulkUpdatingExpansion = false;
        }

        RefreshExpansionProperties();
    }

    public void RemoveCompletedPaths(
        IReadOnlyList<DuplicateGroup> snapshot,
        IReadOnlySet<string> removedPaths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(removedPaths);

        ReplaceFromModels(snapshot
            .Select(group => RebuildGroup(
                group,
                group.Files.Where(file => !removedPaths.Contains(file.Path)).ToArray())));
    }

    public void RestoreExistingPaths(IReadOnlyList<DuplicateGroup> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ReplaceFromModels(snapshot
            .Select(group => RebuildGroup(
                group,
                group.Files.Where(file => File.Exists(file.Path)).ToArray())));
    }

    public void SynchronizeDeletedPaths(IEnumerable<string> paths)
    {
        var removed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SynchronizeModels(Groups.Select(group => group.Model with
        {
            Files = group.Model.Files.Where(file => !removed.Contains(file.Path)).ToArray(),
        }));
    }

    public void RestoreCompletedPaths(IReadOnlyList<DuplicateGroup> snapshot, IReadOnlySet<string> remainingDeletedPaths)
    {
        SynchronizeModels(snapshot.Select(group => group with
        {
            Files = group.Files.Where(file => !remainingDeletedPaths.Contains(file.Path)).ToArray(),
        }));
    }

    private void SynchronizeModels(IEnumerable<DuplicateGroup> models)
    {
        var selected = Groups.SelectMany(group => group.Files).Where(file => file.IsSelected)
            .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expanded = Groups.Where(group => group.IsExpanded).Select(group => (group.Model.Sha256, group.Model.FileSize)).ToHashSet();
        var rebuilt = models.Select(group => RebuildGroup(group, group.Files)).Where(group => group is not null).Cast<DuplicateGroup>().ToArray();
        var prepared = PrepareGroups(rebuilt);
        foreach (var group in prepared)
        {
            group.IsExpanded = expanded.Contains((group.Model.Sha256, group.Model.FileSize));
            foreach (var file in group.Files) file.IsSelected = selected.Contains(file.Path);
        }
        Replace(prepared);
    }

    private void ReplaceFromModels(IEnumerable<DuplicateGroup?> models)
    {
        var groupsToShow = models
            .Where(group => group is not null)
            .Cast<DuplicateGroup>()
            .ToArray();
        Replace(PrepareGroups(groupsToShow));
    }

    private DuplicateGroup? RebuildGroup(
        DuplicateGroup source,
        IReadOnlyList<DuplicateFile> remainingFiles)
    {
        if (remainingFiles.Count < 2)
        {
            return null;
        }

        var selection = selectionService.Select(remainingFiles.ToArray());
        return new DuplicateGroup(
            source.Sha256,
            source.FileSize,
            source.Section,
            remainingFiles,
            selection.KeepPath,
            selection.DeletePaths);
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DuplicateFileViewModel.IsSelected)
            && !isBulkUpdatingSelection)
        {
            RefreshSelectionProperties();
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DuplicateGroupViewModel.IsExpanded)
            && !isBulkUpdatingExpansion)
        {
            RefreshExpansionProperties();
        }
    }

    private void RefreshAllProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(DuplicateGroupCount));
        OnPropertyChanged(nameof(DuplicateFileCount));
        OnPropertyChanged(nameof(DuplicateSizeText));
        RefreshSelectionProperties();
        RefreshExpansionProperties();
    }

    private void RefreshSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedSizeText));
    }

    private void RefreshExpansionProperties()
    {
        OnPropertyChanged(nameof(ShowExpandAllIcon));
        OnPropertyChanged(nameof(ShowCollapseAllIcon));
        OnPropertyChanged(nameof(ExpandCollapseToolTip));
    }
}
