namespace Sims4ModSieve.Core.Duplicates;

public sealed class ApplicationDirectoryGuard(string? directory = null, Action<string>? onRefusal = null)
{
    private static readonly HashSet<string> OwnedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Assets", "licenses", "cs", "de", "en", "en-US", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant",
    };
    private static readonly HashSet<string> OwnedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sims4ModSieve.exe", "createdump.exe", "BUILD-INFO.txt", "SHA256SUMS.txt", "LICENSE", "README.md", "THIRD-PARTY-NOTICES.md",
    };

    public string DirectoryPath { get; } = PathRules.Normalize(directory ?? AppContext.BaseDirectory);

    public bool Contains(string path)
    {
        var normalized = PathRules.Normalize(path);
        if (!PathRules.IsStrictDescendant(normalized, DirectoryPath)) return false;
        var relative = Path.GetRelativePath(DirectoryPath, normalized);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (OwnedDirectories.Contains(first)) return true;
        if (!PathRules.Comparer.Equals(Path.GetDirectoryName(normalized), DirectoryPath)) return false;
        var name = Path.GetFileName(normalized);
        if (name.EndsWith(".off", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return OwnedFiles.Contains(name) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsLocatedWithin(string root) => PathRules.IsWithin(DirectoryPath, PathRules.Normalize(root));

    public bool IsLocatedAt(string root) => PathRules.Comparer.Equals(DirectoryPath, PathRules.Normalize(root));

    public void EnsureAllowed(string path)
    {
        if (!Contains(path)) return;
        onRefusal?.Invoke(path);
        throw new IOException("程序自身的文件不能由工具改动");
    }
}
