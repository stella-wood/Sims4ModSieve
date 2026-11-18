using System.Text.Json;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Settings;

public sealed record IgnoredFileRecord(string Path, long Size, DateTime IgnoredAtUtc)
{
    public static string GetStorePath(string directory) => System.IO.Path.Combine(directory, "file-management.json");

    public static IReadOnlyList<IgnoredFileRecord> Read(string directory)
    {
        var path = GetStorePath(directory);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Ignored", out var ignored))
                throw new InvalidDataException($"忽略列表格式无效：{path}");
            return Validate(ignored.Deserialize<IgnoredFileRecord[]>());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"忽略列表格式无效：{path}", exception);
        }
    }

    public static IReadOnlyList<IgnoredFileRecord> Validate(IReadOnlyList<IgnoredFileRecord>? records)
    {
        if (records is null || records.Any(item => item?.Path is null || !System.IO.Path.IsPathFullyQualified(item.Path) || item.Size < 0))
            throw new InvalidDataException("忽略列表格式无效");
        try
        {
            return records.DistinctBy(item => PathRules.Normalize(item.Path), PathRules.Comparer).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("忽略列表中的路径无效", exception);
        }
    }
}
