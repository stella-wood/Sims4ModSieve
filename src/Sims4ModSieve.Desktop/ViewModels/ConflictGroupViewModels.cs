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
    public string TechnicalMetadata => Stamp is { } stamp
        ? $"修改时间：{stamp.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 文件大小：{stamp.Length:N0} 字节"
        : "修改时间未知 · 文件大小未知";
}

public sealed record ConflictCard(string Key, string Type, string Title, ConflictPairPriority Priority,
    IReadOnlyList<ConflictFile> Files, string Explanation,
    PackageConflictGroup? Package = null, ScriptModuleCollision? Module = null, DuplicateGroup? Duplicate = null)
{
    public int GroupIndex { get; init; }
    public bool HasRemovedFiles { get; init; }
    public IReadOnlyList<ResourceKey> ResourceKeys => Package?.ResourceKeys ?? [];
}

public sealed record ConflictTechnicalCell(string FileName, string Version, string Evidence, ConflictTechnicalTableState? Table = null)
{
    public bool IsType { get; init; }
    public bool IsHeader { get; init; }
    public bool IsRelated { get; init; }
    public bool IsIdenticalVersion { get; init; }
    public bool IsFile => !IsType && !IsRelated;
    public IReadOnlyList<ConflictGroupLink> RelatedGroups { get; init; } = [];
}
public sealed record ConflictTechnicalEntry(string Identifier, string TypeName, IReadOnlyList<ConflictTechnicalCell> Cells)
{
    public bool HasDifferentVersions { get; init; }
    public bool HasIdenticalVersions { get; init; }
    public IReadOnlyList<ConflictGroupLink> RelatedGroups { get; init; } = [];
    public string TableIdentifier => Identifier.Count(character => character == ':') == 2
        ? Identifier.Insert(Identifier.LastIndexOf(':') + 1, "\n") : Identifier;
    public string CopyText => Identifier + "\t" + TypeName + "\n\n" + string.Join("\n\n", Cells.Select(cell =>
        cell.FileName + "\t" + cell.Version + "\t" + cell.Evidence));
}

public sealed class ConflictTechnicalTableState(int fileCount, bool hasRelatedGroups) : ObservableObject
{
    public const double ColumnWidth = 96;
    public const double TypeWidth = 80;
    public const double RelatedWidth = 224;
    private double horizontalOffset;
    private double viewportWidth;
    private double headerHeight = 64;

    public ConflictResultRow? HeaderRow { get; set; }
    public bool HasRelatedGroups { get; } = hasRelatedGroups;
    public double HeaderHeight { get => headerHeight; set => SetProperty(ref headerHeight, value); }

    public double HorizontalOffset
    {
        get => horizontalOffset;
        set => SetProperty(ref horizontalOffset, value);
    }

    public double ViewportWidth
    {
        get => viewportWidth;
        set
        {
            if (!SetProperty(ref viewportWidth, value)) return;
            OnPropertyChanged(nameof(ScrollableWidth));
            OnPropertyChanged(nameof(CellWidth));
            OnPropertyChanged(nameof(ContentWidth));
        }
    }

    public double CellWidth => Math.Max(ColumnWidth, (ViewportWidth - TypeWidth - (HasRelatedGroups ? RelatedWidth : 0)) / fileCount);
    public double ContentWidth => Math.Max(ViewportWidth, TypeWidth + fileCount * ColumnWidth + (HasRelatedGroups ? RelatedWidth : 0));
    public double ScrollableWidth => Math.Max(0, TypeWidth + fileCount * ColumnWidth + (HasRelatedGroups ? RelatedWidth : 0) - ViewportWidth);
}

public sealed record ConflictGroupLink(string Key, int GroupIndex)
{
    public string TechnicalLabel => $"第 {GroupIndex} 组";
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
