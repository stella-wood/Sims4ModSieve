using System.IO;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? duplicateLinkTarget;

    public RelayCommand ScanLinkTargetCommand { get; }

    public string? DuplicateLinkTarget
    {
        get => duplicateLinkTarget;
        private set
        {
            if (SetProperty(ref duplicateLinkTarget, value)) RefreshLinkTargetActions();
        }
    }

    public bool ShowDuplicateScanLinkTarget => DuplicateLinkTarget is { } target && CanAddLinkTarget(target, Sources);

    private static bool CanAddLinkTarget(string target, IEnumerable<ScanSourceViewModel> sources)
    {
        try
        {
            if (!Directory.Exists(target) || new ApplicationDirectoryGuard().Contains(target)
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var source in sources.Where(source => source.IsEnabled))
            {
                var relative = Path.GetRelativePath(source.Path, target);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var current = target;
                while ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0)
                {
                    if (PathRulesEqual(current, source.Path)) return false;
                    current = Path.GetDirectoryName(current)!;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void ScanLinkTarget(object? parameter)
    {
        if (!ScanLinkTargetCommand.CanExecute(parameter) || parameter is not string target) return;
        var existing = ActiveSources.FirstOrDefault(source => PathRulesEqual(source.Path, target));
        if (existing is null) AddSource(ActiveSources, target);
        else existing.IsEnabled = true;
    }

    private void RefreshLinkTargetActions()
    {
        OnPropertyChanged(nameof(ShowDuplicateScanLinkTarget));
        ScanLinkTargetCommand?.RaiseCanExecuteChanged();
        if (conflictRows.Any(row => row.LinkTarget is not null)) PublishConflictRows(conflictRows, visibleConflictParentCount);
    }
}
