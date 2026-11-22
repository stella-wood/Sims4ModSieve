namespace Sims4ModSieve.Core.Duplicates;

public sealed class ScanCoverage
{
    private readonly Dictionary<string, HashSet<string>> directories = new(StringComparer.Ordinal);
    private static readonly StringComparer EntryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool HasDirectories => directories.Count > 0;

    internal void Observe(string directory, IReadOnlyList<string> entries)
    {
        if (!directories.TryGetValue(directory, out var names))
            directories.Add(directory, names = new(EntryComparer));
        foreach (var entry in entries)
            names.Add(Path.GetFileName(entry));
    }

    public bool ProvesMissing(string path)
    {
        // An absent child proves its subtree is gone. A child that was seen but not
        // traversed may be a link or an inaccessible directory, so retain its records.
        while (Path.GetDirectoryName(path) is { } directory)
        {
            if (directories.TryGetValue(directory, out var names))
                return !names.Contains(Path.GetFileName(path));
            path = directory;
        }
        return false;
    }
}
