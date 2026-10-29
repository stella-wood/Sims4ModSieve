using System.Collections.ObjectModel;
using System.Windows.Input;
using Sims4ModSieve.Desktop.Infrastructure;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed class ManagedFileRow : ObservableObject
{
    private bool isSelected;
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public required string SizeText { get; init; }
    public required string TimeText { get; init; }
    public string TimeTooltip { get; init; } = string.Empty;
    public bool CanRestore { get; init; }
    public bool CanRemove { get; init; }
    public bool CanDelete { get; init; }
    public string RestoreText { get; init; } = "恢复";
    public string RemoveText { get; init; } = "取消忽略";
    public AsyncRelayCommand? RestoreCommand { get; init; }
    public RelayCommand? RemoveCommand { get; init; }
    public AsyncRelayCommand? DeleteCommand { get; init; }
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
}

public sealed class FileManagementSectionViewModel : ObservableObject
{
    private bool isMultiSelect;
    public required string Title { get; init; }
    public required string TimeHeader { get; init; }
    public string EmptyText { get; init; } = "暂无文件";
    public bool HasFolder { get; init; }
    public bool HasAdd { get; init; }
    public bool IsIgnored { get; init; }
    public bool IsDisabled { get; init; }
    public bool HasRestore => !IsIgnored;
    public bool HasDelete => IsDisabled || IsIgnored;
    public string BulkActionText => IsDisabled ? "启用所选" : IsIgnored ? "取消忽略" : "恢复所选";
    public ObservableCollection<ManagedFileRow> Files { get; } = [];
    public bool HasFiles => Files.Count > 0;
    public bool HasMultiSelect => HasFiles;
    public bool IsEmpty => !HasFiles;
    public bool HasSelection => Files.Any(row => row.IsSelected);
    public string SelectionText => $"已选择 {Files.Count(row => row.IsSelected)} 项";
    public string MultiSelectText => IsMultiSelect ? "取消多选" : "多选";
    public bool IsMultiSelect
    {
        get => isMultiSelect;
        private set
        {
            if (!SetProperty(ref isMultiSelect, value)) return;
            if (!value) foreach (var row in Files) row.IsSelected = false;
            OnPropertyChanged(nameof(MultiSelectText));
        }
    }
    public RelayCommand ToggleMultiSelectCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand? OpenFolderCommand { get; init; }
    public ICommand? AddCommand { get; init; }
    public AsyncRelayCommand? BulkRestoreCommand { get; set; }
    public AsyncRelayCommand? BulkDeleteCommand { get; set; }
    public RelayCommand? BulkRemoveCommand { get; set; }

    public FileManagementSectionViewModel()
    {
        ToggleMultiSelectCommand = new(_ => IsMultiSelect = !IsMultiSelect, _ => HasMultiSelect);
        SelectAllCommand = new(_ => { foreach (var row in Files) row.IsSelected = true; });
        ClearSelectionCommand = new(_ => { foreach (var row in Files) row.IsSelected = false; });
    }

    public void Replace(IEnumerable<ManagedFileRow> rows)
    {
        var selected = Files.Where(row => row.IsSelected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Files.Clear();
        foreach (var row in rows)
        {
            row.IsSelected = selected.Contains(row.Id);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(ManagedFileRow.IsSelected)) return;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionText));
                BulkRestoreCommand?.RaiseCanExecuteChanged();
                BulkDeleteCommand?.RaiseCanExecuteChanged();
                BulkRemoveCommand?.RaiseCanExecuteChanged();
            };
            Files.Add(row);
        }
        BulkRestoreCommand?.RaiseCanExecuteChanged();
        BulkDeleteCommand?.RaiseCanExecuteChanged();
        BulkRemoveCommand?.RaiseCanExecuteChanged();
        if (!HasFiles) IsMultiSelect = false;
        ToggleMultiSelectCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasMultiSelect));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }
}
