using System.IO;
using System.Text.Json;

namespace Sims4ModSieve.Desktop.Services;

public sealed record IgnoredFileRecord(string Path, long Size, DateTime IgnoredAtUtc);
public sealed record DeletedFileRecord(string Id, RecycleBinItem Item, long Size, DateTime DeletedAtUtc,
    DateTime? LastWriteTimeUtc = null, DisabledModRecord? DisabledMod = null);
public sealed record FileManagementData(IReadOnlyList<IgnoredFileRecord> Ignored,
    IReadOnlyList<DeletedFileRecord> Deleted);

public sealed class FileManagementStore(string? directory = null)
{
    private readonly object gate = new();
    private readonly string? path = directory is null ? null : Path.Combine(directory, "file-management.json");
    private FileManagementData memory = new([], []);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileManagementData Load()
    {
        lock (gate)
        {
            if (path is null) return memory;
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return memory; }
            var data = JsonSerializer.Deserialize<FileManagementData>(json, JsonOptions)
                ?? throw new InvalidDataException("删除与忽略配置为空");
            if (data.Ignored is null || data.Deleted is null
                || data.Ignored.Any(item => item is null || !Path.IsPathFullyQualified(item.Path) || item.Size < 0)
                || data.Deleted.Any(item => item is null || item.Item is null || !Path.IsPathFullyQualified(item.Item.OriginalPath)
                    || item.Item.AbsolutePidl is null || item.Item.AbsolutePidl.Length < 2 || item.Size < 0 || string.IsNullOrWhiteSpace(item.Id)
                    || item.DisabledMod is { } disabled && (!ModDisableService.IsValidRecord(disabled)
                        || !StringComparer.OrdinalIgnoreCase.Equals(disabled.DisabledPath, item.Item.OriginalPath)
                        || disabled.Size != item.Size || disabled.LastWriteTimeUtc != item.LastWriteTimeUtc)))
                throw new InvalidDataException("删除与忽略配置格式无效");
            return memory = data with { Ignored = data.Ignored.DistinctBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase).ToArray() };
        }
    }

    public FileManagementData Update(Func<FileManagementData, FileManagementData> update)
    {
        lock (gate)
        {
            var next = update(Load());
            if (path is not null)
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, Path.GetRandomFileName());
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
                    File.Move(temporary, path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return memory = next;
        }
    }
}
