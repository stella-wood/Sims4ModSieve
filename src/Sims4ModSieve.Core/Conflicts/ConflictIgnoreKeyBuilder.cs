using System.Security.Cryptography;
using System.Text;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Conflicts;

public static class ConflictIgnoreKeyBuilder
{
    public static string Build(IEnumerable<string> relativePaths, IEnumerable<ResourceKey> resourceKeys)
    {
        var paths = relativePaths.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            // 根身份由调用方提供；这里仅统一 Windows 分隔符和大小写。
            if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\')
                || (path.Length >= 2 && path[1] == ':'))
                throw new ArgumentException("忽略键需要相对路径。", nameof(relativePaths));
            return path.Replace('/', '\\').ToUpperInvariant();
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var keys = resourceKeys.Distinct().OrderBy(key => key.Type).ThenBy(key => key.Group)
            .ThenBy(key => key.Instance).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(paths.Length);
            foreach (var path in paths) writer.Write(path);
            writer.Write(keys.Length);
            foreach (var key in keys)
            {
                writer.Write(key.Type);
                writer.Write(key.Group);
                writer.Write(key.Instance);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
