namespace Sims4ModSieve.Core.Duplicates;

public sealed class ApplicationDirectoryGuard(string? directory = null, Action<string>? onRefusal = null)
{
    public string DirectoryPath { get; } = PathRules.Normalize(directory ?? AppContext.BaseDirectory);

    public bool Contains(string path) => PathRules.IsWithin(PathRules.Normalize(path), DirectoryPath);

    public bool IsLocatedWithin(string root) => PathRules.IsWithin(DirectoryPath, PathRules.Normalize(root));

    public void EnsureAllowed(string path)
    {
        if (!Contains(path)) return;
        onRefusal?.Invoke(path);
        throw new IOException("程序目录中的文件不能由工具改动，请将程序移出 Mods 或 Tray 文件夹后重试");
    }
}
