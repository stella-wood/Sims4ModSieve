using System.IO;
using System.Security.Cryptography;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Desktop.Services;

public sealed record FileDeletionCandidate(
    string Path,
    long ExpectedSize,
    DateTime ExpectedLastWriteTimeUtc,
    string GroupLabel,
    DisabledModRecord? DisabledMod = null,
    FileIdentity? ExpectedIdentity = null,
    string? ExpectedSha256 = null);

public sealed record FileActionFailure(string Path, string Message);

public sealed record FileDeletionPreflight(
    IReadOnlyList<FileDeletionCandidate> Candidates,
    IReadOnlyList<FileActionFailure> Problems,
    IReadOnlyList<string> FullySelectedGroups)
{
    public bool CanExecute => Candidates.Count > 0 && Problems.Count == 0;

    public int FileCount => Candidates.Count;

    public long TotalBytes => Candidates.Sum(candidate => candidate.ExpectedSize);
}

public sealed record FileActionBatchResult(
    IReadOnlyList<string> CompletedPaths,
    IReadOnlyList<FileActionFailure> Failures,
    bool CanUndo,
    IReadOnlyList<DeletedFileRecord>? RestoredRecords = null,
    IReadOnlyList<string>? RestoredUndoPaths = null);

public sealed record RecycleBinItem(string OriginalPath, byte[] AbsolutePidl, string? RecycledPath = null);

public sealed record RecycleBinPresence(IReadOnlyList<RecycleBinItem> PresentItems,
    IReadOnlyList<RecycleBinItem> MissingItems, IReadOnlyList<FileActionFailure> Failures);
public sealed record DeletedFilesResult(IReadOnlyList<DeletedFileRecord> Files, IReadOnlyList<FileActionFailure> Failures);

public sealed record RecycleBinDeleteResult(
    IReadOnlyList<RecycleBinItem> CompletedItems,
    IReadOnlyList<FileActionFailure> Failures);

public interface IFileMetadataAccess
{
    bool FileExists(string path);

    FileStamp GetFileStamp(string path);

    Stream OpenRead(string path);
}

public sealed class PhysicalFileMetadataAccess : IFileMetadataAccess
{
    public bool FileExists(string path) => File.Exists(path);

    public FileStamp GetFileStamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc, PhysicalFileIdentity.Read(path));
    }

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
}

public interface IRecycleBinAdapter
{
    bool CanRecycle(string path);

    Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RecycleBinPresence([], [], [new("", "无法核对回收站状态")]));

    Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    Task<FileActionBatchResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken = default);
}

public interface IFileActionService
{
    bool CanUndoLastDelete { get; }

    Task<DeletedFilesResult> GetRecentlyDeletedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeletedFilesResult([], []));

    Task<FileActionBatchResult> RestoreDeletedAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileActionBatchResult([], [], CanUndoLastDelete));

    void ClearLastDelete();

    FileDeletionPreflight Preflight(
        IReadOnlyList<FileDeletionCandidate> candidates,
        IReadOnlyList<string> fullySelectedGroups);

    Task<FileActionBatchResult> DeleteAsync(
        FileDeletionPreflight preflight,
        CancellationToken cancellationToken = default);

    Task<FileActionBatchResult> UndoLastDeleteAsync(CancellationToken cancellationToken = default);
}

public sealed class FileActionService(
    IRecycleBinAdapter recycleBin,
    IFileMetadataAccess metadata,
    FileManagementStore? historyStore = null,
    ModDisableService? modDisableService = null,
    string? applicationDirectory = null,
    Action<string>? logProtectedPath = null) : IFileActionService
{
    private readonly ApplicationDirectoryGuard applicationGuard = new(applicationDirectory,
        logProtectedPath ?? UnexpectedErrorHandler.LogProtectedPath);
    private readonly FileManagementStore historyStore = historyStore ?? new FileManagementStore();
    private List<DeletedFileRecord>? history;
    private IReadOnlyList<RecycleBinItem> lastDeletedItems = Array.Empty<RecycleBinItem>();

    public FileActionService()
        : this(new WindowsRecycleBinAdapter(), new PhysicalFileMetadataAccess(),
            new FileManagementStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve")),
            new ModDisableService())
    {
    }

    public bool CanUndoLastDelete => lastDeletedItems.Count > 0;

    public void ClearLastDelete()
    {
        lastDeletedItems = Array.Empty<RecycleBinItem>();
    }

    public FileDeletionPreflight Preflight(
        IReadOnlyList<FileDeletionCandidate> candidates,
        IReadOnlyList<string> fullySelectedGroups)
    {
        var uniqueCandidates = candidates
            .GroupBy(candidate => Path.GetFullPath(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Path = group.Key })
            .ToArray();
        var problems = new List<FileActionFailure>();

        foreach (var candidate in uniqueCandidates)
        {
            try { applicationGuard.EnsureAllowed(candidate.Path); }
            catch (IOException exception)
            {
                problems.Add(new(candidate.Path, exception.Message));
                continue;
            }
            if (candidate.DisabledMod is { } disabled && (!ModDisableService.IsValidRecord(disabled)
                || !StringComparer.OrdinalIgnoreCase.Equals(disabled.DisabledPath, candidate.Path)
                || disabled.Size != candidate.ExpectedSize || disabled.LastWriteTimeUtc != candidate.ExpectedLastWriteTimeUtc
                || disabled.Identity != candidate.ExpectedIdentity))
            {
                problems.Add(new(candidate.Path, "禁用记录与所选文件不匹配"));
                continue;
            }
            if (!metadata.FileExists(candidate.Path))
            {
                problems.Add(new FileActionFailure(candidate.Path, "文件已不存在，请重新扫描"));
                continue;
            }

            FileStamp currentStamp;
            try
            {
                currentStamp = metadata.GetFileStamp(candidate.Path);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add(new FileActionFailure(candidate.Path, exception.Message));
                continue;
            }

            if (candidate.ExpectedIdentity is null || currentStamp.Identity is null)
            {
                problems.Add(new FileActionFailure(candidate.Path, "缺少可靠文件身份，未操作；请重新扫描或人工核对旧记录"));
                continue;
            }
            if (currentStamp.Length != candidate.ExpectedSize
                || currentStamp.LastWriteTimeUtc != candidate.ExpectedLastWriteTimeUtc
                || currentStamp.Identity != candidate.ExpectedIdentity)
            {
                problems.Add(new FileActionFailure(candidate.Path, "文件在扫描后发生变化，请重新扫描"));
                continue;
            }

            if (!recycleBin.CanRecycle(candidate.Path))
            {
                problems.Add(new FileActionFailure(candidate.Path, "该位置不支持可靠的 Windows 回收站操作"));
            }
        }

        return new FileDeletionPreflight(
            uniqueCandidates,
            problems,
            fullySelectedGroups.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<FileActionBatchResult> DeleteAsync(
        FileDeletionPreflight preflight,
        CancellationToken cancellationToken = default)
    {
        EnsureHistory();
        var ready = new List<FileDeletionCandidate>();
        var preparationFailures = new List<FileActionFailure>();
        await Task.Run(async () =>
        {
            foreach (var candidate in preflight.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refreshed = Preflight([candidate], []);
                if (!refreshed.CanExecute) { preparationFailures.AddRange(refreshed.Problems); continue; }
                try
                {
                    if (candidate.ExpectedSha256 is { } expectedHash)
                    {
                        await using var stream = metadata.OpenRead(candidate.Path);
                        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, expectedHash))
                        {
                            preparationFailures.Add(new(candidate.Path, "文件内容在扫描后发生变化，请重新扫描"));
                            continue;
                        }
                        refreshed = Preflight([candidate], []);
                        if (!refreshed.CanExecute) { preparationFailures.AddRange(refreshed.Problems); continue; }
                    }
                    ready.Add(candidate);
                }
                catch (Exception exception) when (IsHistoryException(exception) || exception is InvalidOperationException)
                { preparationFailures.Add(new(candidate.Path, "文件校验或禁用删除记录失败：" + exception.Message)); }
            }
            foreach (var candidate in ready.ToArray())
            {
                var finalCheck = Preflight([candidate], []);
                if (finalCheck.CanExecute) continue;
                ready.Remove(candidate);
                preparationFailures.AddRange(finalCheck.Problems);
            }
            foreach (var candidate in ready.ToArray())
            {
                if (candidate.DisabledMod is not { } disabled) continue;
                try
                {
                    if (modDisableService is null) throw new InvalidOperationException("未连接禁用记录服务");
                    modDisableService.PrepareRecycle([disabled]);
                }
                catch (Exception exception) when (IsHistoryException(exception) || exception is InvalidOperationException)
                {
                    ready.Remove(candidate);
                    preparationFailures.Add(new(candidate.Path, "禁用删除记录未保存：" + exception.Message));
                }
            }
            }, cancellationToken).ConfigureAwait(false);
        if (ready.Count == 0) return new([], preparationFailures, CanUndoLastDelete);
        var result = await recycleBin
            .MoveToRecycleBinAsync(
                ready.Select(candidate => candidate.Path).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.CompletedItems.Count > 0)
        {
            lastDeletedItems = result.CompletedItems;
            var now = DateTime.UtcNow;
            foreach (var item in result.CompletedItems)
            {
                var deleted = new DeletedFileRecord(Guid.NewGuid().ToString("N"), item, ready.Single(candidate =>
                    StringComparer.OrdinalIgnoreCase.Equals(candidate.Path, item.OriginalPath)).ExpectedSize, now,
                    ready.Single(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Path, item.OriginalPath)).ExpectedLastWriteTimeUtc,
                    ready.Single(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Path, item.OriginalPath)).DisabledMod);
                history!.Add(deleted);
                if (deleted.DisabledMod is not null)
                {
                    try { modDisableService!.RememberRecycled(deleted); }
                    catch (Exception exception) when (IsHistoryException(exception))
                    { preparationFailures.Add(new(item.OriginalPath, "禁用回收记录未保存：" + exception.Message)); }
                }
            }
        }
        return new FileActionBatchResult(
            result.CompletedItems.Select(item => item.OriginalPath).ToArray(),
            result.Failures.Concat(preparationFailures).Concat(SaveHistory()).ToArray(),
            CanUndoLastDelete);
    }

    public async Task<FileActionBatchResult> UndoLastDeleteAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lastDeletedItems.Count == 0)
        {
            return new FileActionBatchResult([], [], false);
        }

        EnsureHistory();
        var restoring = lastDeletedItems;
        var paths = new List<string>();
        var failures = new List<FileActionFailure>();
        var restoredRecords = new List<DeletedFileRecord>();
        for (var index = 0; index < restoring.Count; index++)
        {
            var item = restoring[index];
            var record = history!.FirstOrDefault(entry => SameItem(item, entry.Item));
            FileActionBatchResult restored;
            try { restored = await RestoreItemAsync(item, record?.DisabledMod, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (paths.Count > 0)
            {
                failures.AddRange(restoring.Skip(index).Select(remaining => new FileActionFailure(remaining.OriginalPath, "恢复已取消，文件仍在回收站")));
                break;
            }
            catch (Exception exception) when (IsHistoryException(exception))
            {
                failures.Add(new(item.OriginalPath, exception.Message));
                continue;
            }
            paths.AddRange(restored.CompletedPaths);
            failures.AddRange(restored.Failures);
            if (!restored.CompletedPaths.Contains(item.OriginalPath, StringComparer.OrdinalIgnoreCase)) continue;
            if (record is not null) { restoredRecords.Add(record); history!.Remove(record); }
            lastDeletedItems = lastDeletedItems.Where(pending => !SameItem(pending, item)).ToArray();
            failures.AddRange(SaveHistory());
        }
        return new FileActionBatchResult(paths, failures, CanUndoLastDelete, restoredRecords, paths);
    }
    private void EnsureHistory()
    {
        if (history is not null) return;
        var records = historyStore.Load().Deleted.ToList();
        if (modDisableService is not null)
            foreach (var recovered in modDisableService.GetRecycledRecords())
                if (!records.Any(record => SameItem(record.Item, recovered.Item))) records.Add(recovered);
        history = records;
    }

    private IReadOnlyList<FileActionFailure> SaveHistory()
    {
        try { historyStore.Update(data => data with { Deleted = history!.ToArray() }); return []; }
        catch (Exception exception) when (IsHistoryException(exception))
        { return [new("", "删除记录未保存：" + exception.Message)]; }
    }

    private static bool SameItem(RecycleBinItem first, RecycleBinItem second) =>
        first.AbsolutePidl.AsSpan().SequenceEqual(second.AbsolutePidl);

    private static bool IsHistoryException(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException;

    private async Task<FileActionBatchResult> RestoreItemAsync(RecycleBinItem item, DisabledModRecord? disabled,
        CancellationToken cancellationToken)
    {
        var failures = new List<FileActionFailure>();
        if (disabled is not null)
        {
            try { applicationGuard.EnsureAllowed(disabled.DisabledPath); }
            catch (IOException exception)
            { return new([], [new(item.OriginalPath, exception.Message)], CanUndoLastDelete); }
        }
        try { applicationGuard.EnsureAllowed(item.OriginalPath); }
        catch (IOException exception)
        { return new([], [new(item.OriginalPath, exception.Message)], CanUndoLastDelete); }
        if (disabled is not null)
        {
            try
            {
                if (modDisableService is null) throw new InvalidOperationException("未连接禁用记录服务");
                modDisableService.PrepareRecycleRestore(disabled);
            }
            catch (Exception exception) when (IsHistoryException(exception) || exception is InvalidOperationException)
            { return new([], [new(item.OriginalPath, "无法准备禁用恢复记录：" + exception.Message)], CanUndoLastDelete); }
        }
        var result = await recycleBin.RestoreAsync([item], cancellationToken).ConfigureAwait(false);
        if (disabled is not null)
        {
            try
            {
                if (result.CompletedPaths.Contains(item.OriginalPath, StringComparer.OrdinalIgnoreCase))
                    modDisableService!.CompleteRecycleRestore(disabled);
                else modDisableService!.CancelRecycleRestore(disabled.Id);
            }
            catch (Exception exception) when (IsHistoryException(exception))
            { failures.Add(new(item.OriginalPath, "禁用恢复记录未保存：" + exception.Message)); }
        }
        return result with { Failures = result.Failures.Concat(failures).ToArray() };
    }

    public async Task<DeletedFilesResult> GetRecentlyDeletedAsync(CancellationToken cancellationToken = default)
    {
        EnsureHistory();
        if (history!.Count == 0) return new([], []);
        var result = await recycleBin.QueryAsync(history.Select(record => record.Item).ToArray(), cancellationToken).ConfigureAwait(false);
        var failures = result.Failures.ToList();
        if (modDisableService is not null)
        {
            try
            {
                modDisableService.ForgetRecycled(history.Where(record => record.DisabledMod is not null
                    && result.MissingItems.Any(item => SameItem(item, record.Item))).Select(record => record.Id).ToArray());
            }
            catch (Exception exception) when (IsHistoryException(exception))
            { failures.Add(new("", "禁用回收记录未更新：" + exception.Message)); }
        }
        history.RemoveAll(record => result.MissingItems.Any(item => SameItem(item, record.Item)));
        lastDeletedItems = lastDeletedItems.Where(item => !result.MissingItems.Any(missing => SameItem(item, missing))).ToArray();
        return new(history.OrderByDescending(record => record.DeletedAtUtc).ToArray(), failures.Concat(SaveHistory()).ToArray());
    }

    public async Task<FileActionBatchResult> RestoreDeletedAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHistory();
        var selected = history!.Where(record => ids.Contains(record.Id, StringComparer.Ordinal)).ToArray();
        var presence = await recycleBin.QueryAsync(selected.Select(record => record.Item).ToArray(), cancellationToken).ConfigureAwait(false);
        var completed = new List<string>();
        var failures = new List<FileActionFailure>();
        var restoredRecords = new List<DeletedFileRecord>();
        var restoredUndoPaths = new List<string>();
        var present = selected.Where(record => presence.PresentItems.Any(item => SameItem(item, record.Item))).ToArray();
        for (var index = 0; index < present.Length; index++)
        {
            var record = present[index];
            FileActionBatchResult result;
            try { result = await RestoreItemAsync(record.Item, record.DisabledMod, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (completed.Count > 0)
            {
                failures.AddRange(present.Skip(index).Select(remaining => new FileActionFailure(remaining.Item.OriginalPath, "恢复已取消，文件仍在回收站")));
                break;
            }
            catch (Exception exception) when (IsHistoryException(exception))
            {
                failures.Add(new(record.Item.OriginalPath, exception.Message));
                continue;
            }
            failures.AddRange(result.Failures);
            if (!result.CompletedPaths.Contains(record.Item.OriginalPath, StringComparer.OrdinalIgnoreCase)) continue;
            completed.Add(record.Item.OriginalPath);
            restoredRecords.Add(record);
            if (lastDeletedItems.Any(item => SameItem(item, record.Item))) restoredUndoPaths.Add(record.Item.OriginalPath);
            history!.Remove(record);
            lastDeletedItems = lastDeletedItems.Where(item => !SameItem(item, record.Item)).ToArray();
            failures.AddRange(SaveHistory());
        }
        var missing = presence.MissingItems.Select(item => new FileActionFailure(item.OriginalPath, "找不到回收站中的文件"));
        return new(completed, failures.Concat(presence.Failures).Concat(missing).ToArray(), CanUndoLastDelete, restoredRecords, restoredUndoPaths);
    }

}
