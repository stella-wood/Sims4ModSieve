using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.FileOperations;

public sealed record DisabledRecycleRecord(string Id, RecycleBinItem Item, DateTime DeletedAtUtc);

public sealed record DisabledModRecord(string Id, string OriginalPath, long Size, DateTime LastWriteTimeUtc, DateTime DisabledAtUtc,
    bool RecyclePending = false, bool Recycled = false, bool RestorePending = false, DisabledRecycleRecord? RecycleEntry = null,
    FileIdentity? Identity = null,
    [property: JsonPropertyName("source")] string Source = DisabledModRecord.SieveSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisabledSuffix = null)
{
    public const string SieveSource = "sieve";
    public const string ExternalSource = "external";

    [JsonIgnore]
    public string DisabledPath => OriginalPath + (DisabledSuffix ?? ".off");

    [JsonIgnore]
    public bool IsExternal => Source == ExternalSource;
}

public sealed record DisabledModsResult(IReadOnlyList<DisabledModRecord> Records, IReadOnlyList<FileActionFailure> Failures);
public sealed record ModDisableBatchResult(IReadOnlyList<string> CompletedPaths, IReadOnlyList<FileActionFailure> Failures);

public sealed class ModDisableService(string? directory = null, string? applicationDirectory = null,
    Action<string>? logProtectedPath = null)
{
    private readonly ApplicationDirectoryGuard applicationGuard = new(applicationDirectory,
        logProtectedPath);
    private readonly FileManagementLock writeLock = new(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve"));
    private readonly string ledgerPath = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve"), "disabled-mods.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ExternalSuffixes = [".off", ".disabled", "OFF"];

    public static bool TryGetExternalOriginalPath(string path, [NotNullWhen(true)] out string? original)
    {
        foreach (var suffix in ExternalSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && IsModPath(path[..^suffix.Length]))
            {
                original = path[..^suffix.Length];
                return true;
            }
        }
        original = null;
        return false;
    }

    private static bool ScansMods(string modsRoot, IEnumerable<string> sources) =>
        sources.Any(source => IsWithin(source, modsRoot) || IsWithin(modsRoot, source));

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public DisabledModsResult? RegisterExternalInMods(string modsRoot, IEnumerable<string> scanSources,
        IReadOnlyCollection<string> observedPaths, CancellationToken cancellationToken = default)
    {
        var sources = scanSources.ToArray();
        if (sources.Length == 0) return null;
        var selected = ScansMods(modsRoot, sources);
        string realRoot;
        try { realRoot = ResolveDirectory(modsRoot); }
        catch (Exception exception) when (!selected && IsFileException(exception)) { return null; }
        if (!selected && !ScansMods(realRoot, sources)) return null;

        var paths = observedPaths.Where(path => IsWithin(path, modsRoot) || IsWithin(path, realRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<FileActionFailure>();
        // The content scanners skip linked roots. Enumerate only the resolved Mods root for the ledger.
        if (sources.Any(source => IsWithin(modsRoot, source)) && (File.GetAttributes(modsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            CollectFromLinkedMods(realRoot, paths, failures, cancellationToken);
        }
        var result = RegisterExternal(paths, cancellationToken);
        return failures.Count == 0 ? result : new(result?.Records ?? LoadReadOnly().Records,
            failures.Concat(result?.Failures ?? []).ToArray());
    }

    private void CollectFromLinkedMods(string root, HashSet<string> paths, List<FileActionFailure> failures,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (applicationGuard.Contains(directory)) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception exception) when (IsFileException(exception))
            {
                failures.Add(new(directory, exception.Message));
                continue;
            }
            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (applicationGuard.Contains(path)) continue;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // A cloud directory can be a reparse point without redirecting to another path.
                        if ((attributes & FileAttributes.ReparsePoint) == 0
                            || StringComparer.OrdinalIgnoreCase.Equals(path, ResolveDirectory(path)))
                            pending.Push(path);
                    }
                    else if ((attributes & FileAttributes.ReparsePoint) == 0 && TryGetExternalOriginalPath(path, out _))
                        paths.Add(path);
                }
                catch (Exception exception) when (IsFileException(exception)) { failures.Add(new(path, exception.Message)); }
            }
        }
    }

    public DisabledModsResult? RegisterExternal(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0 && !File.Exists(ledgerPath)) return null;
        using var operation = writeLock.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        var previous = ReadRecords();
        var state = Reconcile(previous, save: false, externalOnly: true);
        var records = state.Records.ToList();
        var failures = state.Failures.ToList();
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, string Original)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetExternalOriginalPath(path, out var original)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(original) || !IsExternalFilePath(original))
                    throw new IOException("外部禁用路径无效，未登记");
                var realOriginal = ResolveExternalPaths(original, path, directories);
                candidates.Add((realOriginal + path[original.Length..], realOriginal));
            }
            catch (Exception exception) when (IsFileException(exception)) { failures.Add(new(path, exception.Message)); }
        }
        var active = records.Where(record => !record.Recycled || record.RestorePending)
            .Select(record =>
            {
                if (candidates.Count > 0)
                {
                    try { return (Path: ResolveFileParent(record.OriginalPath, directories), Record: record); }
                    catch (Exception exception) when (IsFileException(exception)) { }
                }
                return (Path: record.OriginalPath, Record: record);
            })
            .ToLookup(item => item.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var group in candidates.DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .GroupBy(candidate => candidate.Original, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = group.ToArray();
            if (active.Contains(group.Key))
            {
                foreach (var target in targets.Where(target => !active[group.Key].Any(item =>
                    StringComparer.OrdinalIgnoreCase.Equals(group.Key + (item.Record.DisabledSuffix ?? ".off"), target.Path))))
                    failures.Add(new(target.Path, "同一个原路径已有禁用记录，未登记该文件"));
                continue;
            }
            if (targets.Length > 1)
            {
                failures.Add(new(group.Key, "多个外部禁用文件对应同一个原路径，未登记"));
                continue;
            }

            var candidate = targets[0];
            try
            {
                var record = new DisabledModRecord(Guid.NewGuid().ToString("N"), candidate.Original, 0, default, DateTime.UtcNow,
                    Source: DisabledModRecord.ExternalSource, DisabledSuffix: candidate.Path[candidate.Original.Length..]);
                if (!IsValidRecord(record)) throw new IOException("外部禁用路径无效，未登记");
                if (TryReadStamp(candidate.Path) is not { } stamp) continue;
                if (stamp.Identity is null) throw new IOException("无法取得可靠文件身份，未登记");
                records.Add(record with { Size = stamp.Size, LastWriteTimeUtc = stamp.Modified, Identity = stamp.Identity });
            }
            catch (Exception exception) when (IsFileException(exception)) { failures.Add(new(candidate.Path, exception.Message)); }
        }

        if (!records.SequenceEqual(previous))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Save(records); }
            catch (Exception exception) when (IsFileException(exception))
            {
                failures.Add(new("", "外部禁用记录未保存：" + exception.Message));
                return new(previous.Where(record => !record.Recycled).ToArray(), failures);
            }
        }
        else if (failures.Count == 0) return null;
        return new(records.Where(record => !record.Recycled).ToArray(), failures);
    }

    public DisabledModsResult Load()
    {
        using var operation = writeLock.Enter();
        return LoadActive(save: true);
    }

    internal DisabledModsResult LoadReadOnly() => LoadActive(save: false);

    private DisabledModsResult LoadActive(bool save)
    {
        var state = ReadAndReconcile(save);
        return state with { Records = state.Records.Where(record => !record.Recycled).ToArray() };
    }

    public void PrepareRecycle(IReadOnlyList<DisabledModRecord> selected)
    {
        using (writeLock.Enter())
        {
            var records = ReadRecords().ToList();
            foreach (var requested in selected)
            {
                var index = records.FindIndex(record => record.Id == requested.Id && !record.Recycled
                    && record.OriginalPath == requested.OriginalPath && record.Size == requested.Size
                    && record.LastWriteTimeUtc == requested.LastWriteTimeUtc);
                if (index < 0) throw new InvalidDataException("找不到匹配的禁用记录，请刷新");
                if (!StringComparer.OrdinalIgnoreCase.Equals(requested.DisabledPath, records[index].DisabledPath))
                    throw new InvalidDataException("禁用路径与账本不符，请刷新");
                RequireStamp(requested.DisabledPath, records[index]);
                records[index] = records[index] with { RecyclePending = true };
            }
            Save(records);
        }
    }

    public void PrepareRecycleRestore(DisabledModRecord record)
    {
        using (writeLock.Enter())
        {
            if (!IsValidRecord(record)) throw new InvalidDataException("禁用恢复记录格式无效");
            applicationGuard.EnsureAllowed(record.OriginalPath);
            applicationGuard.EnsureAllowed(record.DisabledPath);
            if (record.IsExternal) EnsureExternalPaths(record);
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
        using (writeLock.Enter())
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
        using (writeLock.Enter())
        {
            var records = ReadRecords();
            Save(records.Select(record => record.Id == id ? record with { RestorePending = false } : record).ToArray());
        }
    }

    public void RememberRecycled(DeletedFileRecord deleted)
    {
        using (writeLock.Enter())
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
        using (writeLock.Enter())
            return ReadAndReconcile().Records.Where(record => record.Recycled && record.RecycleEntry is not null)
                .Select(record => new DeletedFileRecord(record.RecycleEntry!.Id, record.RecycleEntry.Item, record.Size,
                    record.RecycleEntry.DeletedAtUtc, record.LastWriteTimeUtc, record with { RecycleEntry = null })).ToArray();
    }

    public void ForgetRecycled(IReadOnlyList<string> ids)
    {
        using (writeLock.Enter())
        {
            var records = ReadRecords();
            var next = records.Where(record => !record.Recycled || record.RecycleEntry is null
                || !ids.Contains(record.RecycleEntry.Id, StringComparer.Ordinal)).ToArray();
            if (next.Length != records.Length) Save(next);
        }
    }

    public ModDisableBatchResult Disable(IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, FileStamp>? expectedStamps = null)
    {
        using (writeLock.Enter())
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
                    applicationGuard.EnsureAllowed(original);
                    if (!seen.Add(original)) continue;
                    if (!IsModPath(original)) throw new InvalidDataException("只能禁用 .package 或 .ts4script 文件");
                    if (records.Any(item => !item.Recycled && StringComparer.OrdinalIgnoreCase.Equals(item.OriginalPath, original)))
                        throw new IOException("该文件已有禁用记录，请先处理原记录");
                    var stamp = ReadStamp(original);
                    if (stamp.Identity is null) throw new IOException("无法取得可靠文件身份，未禁用");
                    if (expectedStamps is not null)
                    {
                        if (!expectedStamps.TryGetValue(original, out var expected)
                            || expected.Identity is null || expected.Length != stamp.Size
                            || expected.LastWriteTimeUtc != stamp.Modified || expected.Identity != stamp.Identity)
                            throw new IOException("文件在扫描后发生变化，请重新扫描");
                    }
                    if (TryReadStamp(original + ".off") is not null) throw new IOException("禁用目标已存在，未覆盖");
                    record = new(Guid.NewGuid().ToString("N"), original, stamp.Size, stamp.Modified, DateTime.UtcNow,
                        Identity: stamp.Identity);
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
        using (writeLock.Enter())
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
                    applicationGuard.EnsureAllowed(record.OriginalPath);
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

    private DisabledModsResult ReadAndReconcile(bool save = true) => Reconcile(ReadRecords(), save);

    private DisabledModsResult Reconcile(DisabledModRecord[] records, bool save, bool externalOnly = false)
    {
        var current = new List<DisabledModRecord>();
        var failures = new List<FileActionFailure>();
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if ((externalOnly && !record.IsExternal) || (record.Recycled && !record.RestorePending))
            {
                current.Add(record);
                continue;
            }
            try
            {
                if (record.IsExternal) ResolveExternalPaths(record.OriginalPath, record.DisabledPath, directories);
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
                    if (record.IsExternal) continue;
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
        if (save && !current.SequenceEqual(records))
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
        try
        {
            using var stream = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
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

    public static bool IsValidRecord(DisabledModRecord record) => Guid.TryParseExact(record.Id, "N", out _)
        && !string.IsNullOrWhiteSpace(record.OriginalPath) && Path.IsPathFullyQualified(record.OriginalPath)
        && IsModPath(record.OriginalPath) && record.Size >= 0
        && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(record.OriginalPath), record.OriginalPath)
        && (record.Source == DisabledModRecord.SieveSource && record.DisabledSuffix is null
            || record.IsExternal && record.DisabledSuffix is not null
                && ExternalSuffixes.Contains(record.DisabledSuffix, StringComparer.OrdinalIgnoreCase)
                && IsExternalFilePath(record.OriginalPath))
        && (record.RecycleEntry is null || !string.IsNullOrWhiteSpace(record.RecycleEntry.Id)
            && record.RecycleEntry.Item is { AbsolutePidl.Length: >= 2 } item
            && StringComparer.OrdinalIgnoreCase.Equals(item.OriginalPath, record.DisabledPath));

    private static bool IsExternalFilePath(string path) =>
        !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && !path[(Path.GetPathRoot(path)?.Length ?? 0)..].Contains(':')
        && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.EndsWith('.') || segment.EndsWith(' '));

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

    internal static bool IsModPath(string path) => Path.GetExtension(path).Equals(".package", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".ts4script", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static (long Size, DateTime Modified, FileIdentity? Identity) ReadStamp(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("目标是文件夹，未改动");
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc, PhysicalFileIdentity.Read(path));
    }

    private static (long Size, DateTime Modified, FileIdentity? Identity)? TryReadStamp(string path)
    {
        try { return ReadStamp(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool Matches((long Size, DateTime Modified, FileIdentity? Identity) stamp, DisabledModRecord record) =>
        record.Identity is not null && stamp.Identity is not null && stamp.Size == record.Size
        && stamp.Modified == record.LastWriteTimeUtc && stamp.Identity == record.Identity;

    private void RequireStamp(string path, DisabledModRecord record)
    {
        applicationGuard.EnsureAllowed(path);
        if (record.IsExternal) EnsureExternalPaths(record);
        if (record.Identity is null) throw new IOException("旧禁用记录缺少文件身份，请人工核对文件与记录后处理");
        if (!Matches(ReadStamp(path), record)) throw new IOException("文件发生变化，未改动，请重新核对");
    }

    internal void EnsureExternalPaths(DisabledModRecord record) => ResolveExternalPaths(record.OriginalPath, record.DisabledPath);

    private string ResolveExternalPaths(string original, string disabled, Dictionary<string, string>? directories = null)
    {
        applicationGuard.EnsureAllowed(original);
        applicationGuard.EnsureAllowed(disabled);
        var resolved = ResolveFileParent(original, directories);
        applicationGuard.EnsureAllowed(resolved);
        applicationGuard.EnsureAllowed(resolved + disabled[original.Length..]);
        return resolved;
    }

    internal static string ResolveFileParent(string path, Dictionary<string, string>? directories = null)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (directories is null || !directories.TryGetValue(parent, out var realParent))
        {
            realParent = ResolveDirectory(parent);
            directories?.Add(parent, realParent);
        }
        return Path.Combine(realParent, Path.GetFileName(path));
    }

    private static string ResolveDirectory(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException("路径不是目录");
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        // Open the directory normally so Windows follows junctions and cloud reparse points.
        const uint backupSemantics = 0x02000000;
        const uint openExisting = 3;
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法打开外部禁用文件的目录", new Win32Exception());
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity && length <= 32_768)
        {
            buffer.EnsureCapacity((int)length);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("无法解析外部禁用文件的目录", new Win32Exception());
        var resolved = buffer.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException("无法解析外部禁用文件的目录");
        var directory = resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + resolved[8..]
            : resolved[4..];
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("无法解析外部禁用文件的目录");
        return directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
