using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Infrastructure;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed record ConflictFile(string Path, FileStamp? Stamp)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool HasStamp => Stamp is not null;
    public string Metadata => Stamp is { } stamp
        ? $"{stamp.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {DuplicateGroupViewModel.FormatBytes(stamp.Length)}"
        : "修改时间未知 · 大小未知";
}

public sealed record ConflictCard(string Key, string Type, string Title, ConflictPairPriority Priority,
    IReadOnlyList<ConflictFile> Files, string Explanation,
    PackageConflictGroup? Package = null, ScriptModuleCollision? Module = null, DuplicateGroup? Duplicate = null)
{
    public int GroupIndex { get; init; }
    public IReadOnlyList<ResourceKey> ResourceKeys => Package?.ResourceKeys ?? [];
}

public sealed record ConflictTechnicalCell(string FileName, string Version, string Evidence);
public sealed record ConflictTechnicalEntry(string Identifier, string TypeName, IReadOnlyList<ConflictTechnicalCell> Cells)
{
    public string CopyText => Identifier + "\t" + TypeName + "\n" + string.Join("\n", Cells.Select(cell =>
        cell.FileName + "\t" + cell.Version + "\t" + cell.Evidence));
}

public sealed record ConflictGroupLink(string Key, int GroupIndex)
{
    public string Label => $"查看第 {GroupIndex} 组 →";
    public string CopyLabel => $"去第{GroupIndex}组 →";
}

public sealed record ConflictRelationship(string Label, string Detail, ConflictGroupLink Link,
    bool ContainsMain = false, bool ContainedByMain = false)
{
    public bool IsContainment => ContainsMain || ContainedByMain;
    public string AccessibleLabel => ContainsMain ? "包含主文件" : ContainedByMain ? "被主文件包含" : Label;
}

public sealed record ConflictRelationshipRow(ConflictFile File, ConflictPairPriority Priority,
    IReadOnlyList<ConflictRelationship> Relationships, IReadOnlyList<ConflictFile> Copies,
    ConflictGroupLink? DuplicateLink)
{
    public string CopiesLabel => $"另有{Copies.Count}份副本";
    public bool HasCopies => Copies.Count > 0;
}

public sealed class ConflictRelationshipsViewModel(ConflictFile file, int cardCount, bool hasContainment,
    IReadOnlyList<ConflictRelationshipRow> related, IReadOnlyList<ConflictFile> copies, ConflictGroupLink? duplicateLink)
    : ObservableObject
{
    public ConflictFile File { get; } = file;
    public int CardCount { get; } = cardCount;
    public string Footer => $"共出现在 {CardCount} 张卡里";
    public bool HasContainment { get; } = hasContainment;
    public IReadOnlyList<ConflictRelationshipRow> Related { get; } = related;
    public IReadOnlyList<ConflictFile> Copies { get; } = copies;
    public bool HasCopies => Copies.Count > 0;
    public string CopiesLabel => $"存在{Copies.Count}份副本，完全相同";
    public ConflictGroupLink? DuplicateLink { get; } = duplicateLink;
}
