using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sims4ModSieve.Desktop.Services;

public sealed record DisabledRecycleRecord(string Id, RecycleBinItem Item, DateTime DeletedAtUtc);

public sealed record DisabledModRecord(string Id, string OriginalPath, long Size, DateTime LastWriteTimeUtc, DateTime DisabledAtUtc,
    bool RecyclePending = false, bool Recycled = false, bool RestorePending = false, DisabledRecycleRecord? RecycleEntry = null)
{
    [JsonIgnore]
    public string DisabledPath => OriginalPath + ".off";
}

public sealed record DisabledModsResult(IReadOnlyList<DisabledModRecord> Records, IReadOnlyList<FileActionFailure> Failures);
public sealed record ModDisableBatchResult(IReadOnlyList<string> CompletedPaths, IReadOnlyList<FileActionFailure> Failures);

public sealed class ModDisableService(string? directory = null)
{
    private readonly object gate = new();
    private readonly string ledgerPath = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve"), "disabled-mods.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DisabledModsResult Load()
    {
        lock (gate)
        {
            var state = ReadAndReconcile();
            return state with { Records = state.Records.Where(record => !record.Recycled).ToArray() };
        }
    }

    public void PrepareRecycle(IReadOnlyList<DisabledModRecord> selected)
    {
        lock (gate)
        {
            var records = ReadRecords().ToList();
            foreach (var requested in selected)
            {
                var index = records.FindIndex(record => record.Id == requested.Id && !record.Recycled
                    && record.OriginalPath == requested.OriginalPath && record.Size == requested.Size
                    && record.LastWriteTimeUtc == requested.LastWriteTimeUtc);
                if (index < 0) throw new InvalidDataException("找不到匹配的禁用记录，请刷新");
                RequireStamp(requested.DisabledPath, records[index]);
                records[index] = records[index] with { RecyclePending = true };
            }
            Save(records);
        }
    }

    public void PrepareRecycleRestore(DisabledModRecord record)
    {
        lock (gate)
        {
            if (!IsValidRecord(record)) throw new InvalidDataException("禁用恢复记录格式无效");
            if (TryReadStamp(record.DisabledPath) is not null) throw new IOException("已禁用路径已有同名文件，未覆盖");
            var records = ReadAndReconcile().Records.ToList();
            if (records.Any(item => item.Id != record.Id && !item.Recycled
                && StringComparer.OrdinalIgnoreCase.Equals(item.OriginalPath, record.OriginalPath)))
                throw new IOException("该路径已有其他禁用记录，未覆盖");
            var entry = records.FirstOrDefault(item => item.Id == record.Id)?.RecycleEntry;
            records.RemoveAll(item => item.Id == record.Id);
            // 仍保持回收状态；Shell 恢复成功后才发布到已禁用列表。
            records.Add(record with { Recycled = true, RecyclePending = false, RestorePending = true, RecycleEntry = entry });
            Save(records);
        }
    }

    public void CompleteRecycleRestore(DisabledModRecord record)
    {
        lock (gate)
        {
            RequireStamp(record.DisabledPath, record);
            var records = ReadAndReconcile().Records.ToList();
            records.RemoveAll(item => item.Id == record.Id);
            records.Add(record with { Recycled = false, RecyclePending = false, RestorePending = false, RecycleEntry = null });
            Save(records);
        }
    }

    public void CancelRecycleRestore(string id)
    {
        lock (gate)
        {
            var records = ReadRecords();
            Save(records.Select(record => record.Id == id ? record with { RestorePending = false } : record).ToArray());
        }
    }

    public void RememberRecycled(DeletedFileRecord deleted)
    {
        lock (gate)
        {
            var records = ReadRecords().ToList();
            var index = records.FindIndex(record => record.Id == deleted.DisabledMod!.Id);
            if (index < 0) throw new InvalidDataException("找不到禁用删除记录");
            records[index] = records[index] with { Recycled = true, RecyclePending = false,
                RecycleEntry = new(deleted.Id, deleted.Item, deleted.DeletedAtUtc) };
            Save(records);
        }
    }

    public IReadOnlyList<DeletedFileRecord> GetRecycledRecords()
    {
        lock (gate)
            return ReadAndReconcile().Records.Where(record => record.Recycled && record.RecycleEntry is not null)
                .Select(record => new DeletedFileRecord(record.RecycleEntry!.Id, record.RecycleEntry.Item, record.Size,
                    record.RecycleEntry.DeletedAtUtc, record.LastWriteTimeUtc, record with { RecycleEntry = null })).ToArray();
    }

    public void ForgetRecycled(IReadOnlyList<string> ids)
    {
        lock (gate)
        {
            var records = ReadRecords();
            var next = records.Where(record => !record.Recycled || record.RecycleEntry is null
                || !ids.Contains(record.RecycleEntry.Id, StringComparer.Ordinal)).ToArray();
            if (next.Length != records.Length) Save(next);
        }
    }

    public ModDisableBatchResult Disable(IReadOnlyList<string> paths)
    {
        lock (gate)
        {
            var state = ReadAndReconcile();
            var records = state.Records.ToList();
            var completed = new List<string>();
            var failures = state.Failures.ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                DisabledModRecord? record = null;
                var recorded = false;
                try
                {
                    var original = Path.GetFullPath(path);
                    if (!seen.Add(original)) continue;
                    if (!IsModPath(original)) throw new InvalidDataException("只能禁用 .package 或 .ts4script 文件");
                    if (records.Any(item => !item.Recycled && StringComparer.OrdinalIgnoreCase.Equals(item.OriginalPath, original)))
                        throw new IOException("该文件已有禁用记录，请先处理原记录");
                    var stamp = ReadStamp(original);
                    if (TryReadStamp(original + ".off") is not null) throw new IOException("禁用目标已存在，未覆盖");
                    record = new(Guid.NewGuid().ToString("N"), original, stamp.Size, stamp.Modified, DateTime.UtcNow);
                    // 先保存恢复所需的信息，进程中断后仍能识别已经改名的文件。
                    Save(records.Append(record).ToArray());
                    records.Add(record);
                    recorded = true;
                    RequireStamp(original, record);
                    File.Move(original, record.DisabledPath);
                    completed.Add(original);
                }
                catch (Exception exception) when (IsFileException(exception))
                {
                    failures.Add(new(path, exception.Message));
                    if (recorded)
                    {
                        records.Remove(record!);
                        try { Save(records); }
                        catch (Exception saveException) when (IsFileException(saveException))
                        {
                            records.Add(record!);
                            failures.Add(new(path, "无法更新禁用清单：" + saveException.Message));
                        }
                    }
                }
            }
            return new(completed, failures);
        }
    }

    public ModDisableBatchResult Restore(IReadOnlyList<string> ids)
    {
        lock (gate)
        {
            var state = ReadAndReconcile();
            var records = state.Records.ToList();
            var completed = new List<string>();
            var failures = state.Failures.ToList();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                var record = records.FirstOrDefault(item => item.Id == id && !item.Recycled);
                if (record is null)
                {
                    failures.Add(new(id, "找不到禁用记录，请刷新"));
                    continue;
                }
                try
                {
                    RequireStamp(record.DisabledPath, record);
                    File.Move(record.DisabledPath, record.OriginalPath);
                    completed.Add(record.OriginalPath);
                    var next = records.Where(item => item.Id != id).ToArray();
                    Save(next);
                    records.Remove(record);
                }
                catch (Exception exception) when (IsFileException(exception))
                {
                    failures.Add(new(record.OriginalPath, exception.Message));
                }
            }
            return new(completed, failures);
        }
    }

    private DisabledModsResult ReadAndReconcile()
    {
        var records = ReadRecords();
        var current = new List<DisabledModRecord>();
        var failures = new List<FileActionFailure>();
        foreach (var record in records)
        {
            if (record.Recycled && !record.RestorePending) { current.Add(record); continue; }
            try
            {
                var disabled = TryReadStamp(record.DisabledPath);
                if (record.RestorePending)
                {
                    current.Add(disabled is { } restoredStamp && Matches(restoredStamp, record)
                        && !records.Any(other => other.Id != record.Id && !other.Recycled
                            && StringComparer.OrdinalIgnoreCase.Equals(other.OriginalPath, record.OriginalPath))
                        ? record with { RestorePending = false, Recycled = false, RecycleEntry = null } : record);
                    continue;
                }
                if (record.RecyclePending)
                {
                    current.Add(record with { RecyclePending = false, Recycled = disabled is null });
                    if (disabled is { } pendingStamp && !Matches(pendingStamp, record))
                        failures.Add(new(record.OriginalPath, "已禁用文件发生变化，未自动恢复"));
                    continue;
                }
                if (disabled is null)
                {
                    var original = TryReadStamp(record.OriginalPath);
                    // 改名前或恢复后中断，磁盘上原文件匹配时可以清除预记账。
                    if (original is { } stamp && Matches(stamp, record)) continue;
                    failures.Add(new(record.OriginalPath, "找不到匹配的已禁用文件，已保留恢复记录"));
                }
                else if (!Matches(disabled.Value, record))
                    failures.Add(new(record.OriginalPath, "已禁用文件发生变化，未自动恢复"));
            }
            catch (Exception exception) when (IsFileException(exception)) { failures.Add(new(record.OriginalPath, exception.Message)); }
            current.Add(record);
        }
        if (!current.SequenceEqual(records))
        {
            try { Save(current); }
            catch (Exception exception) when (IsFileException(exception))
            { failures.Add(new("", "禁用记录未保存：" + exception.Message)); }
        }
        return new(current, failures);
    }

    private DisabledModRecord[] ReadRecords()
    {
        string json;
        try { json = File.ReadAllText(ledgerPath); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return []; }
        var records = JsonSerializer.Deserialize<DisabledModRecord[]>(json, JsonOptions)
            ?? throw new InvalidDataException("禁用清单为空");
        if (records.Any(record => record is null || !IsValidRecord(record))
            || records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != records.Length
            || records.Where(record => !record.Recycled).Select(record => record.OriginalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count(record => !record.Recycled))
            throw new InvalidDataException("禁用清单格式无效");
        return records;
    }

    internal static bool IsValidRecord(DisabledModRecord record) => Guid.TryParseExact(record.Id, "N", out _)
        && !string.IsNullOrWhiteSpace(record.OriginalPath) && Path.IsPathFullyQualified(record.OriginalPath)
        && IsModPath(record.OriginalPath) && record.Size >= 0
        && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(record.OriginalPath), record.OriginalPath)
        && (record.RecycleEntry is null || !string.IsNullOrWhiteSpace(record.RecycleEntry.Id)
            && record.RecycleEntry.Item is { AbsolutePidl.Length: >= 2 } item
            && StringComparer.OrdinalIgnoreCase.Equals(item.OriginalPath, record.DisabledPath));

    private void Save(IReadOnlyList<DisabledModRecord> records)
    {
        var directory = Path.GetDirectoryName(ledgerPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, JsonOptions));
            File.Move(temporary, ledgerPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsModPath(string path) => Path.GetExtension(path).Equals(".package", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".ts4script", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static (long Size, DateTime Modified) ReadStamp(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("目标是文件夹，未改动");
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static (long Size, DateTime Modified)? TryReadStamp(string path)
    {
        try { return ReadStamp(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool Matches((long Size, DateTime Modified) stamp, DisabledModRecord record) =>
        stamp.Size == record.Size && stamp.Modified == record.LastWriteTimeUtc;

    private static void RequireStamp(string path, DisabledModRecord record)
    {
        if (!Matches(ReadStamp(path), record)) throw new IOException("文件发生变化，未改动，请重新核对");
    }
}
