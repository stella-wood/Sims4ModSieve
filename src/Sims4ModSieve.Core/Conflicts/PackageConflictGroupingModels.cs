using System.Collections.ObjectModel;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Conflicts;

public enum PackageConflictRelationKind { BundleContainment, SuspectedSameMod, OverrideConflict, Hidden }

public sealed record PackageConflictGroupingOptions(
    int MinimumContainedResourceCount,
    OverrideConflictCardRule OverrideCardRule,
    double SameModThreshold = 0.5)
{
    public const int DefaultMinimumContainedResourceCount = 3;

    public PackageConflictGroupingOptions()
        : this(new OverrideConflictCardRule(OverrideConflictCardRuleKind.ByExactSharedKeySet), 0.5) { }

    public PackageConflictGroupingOptions(OverrideConflictCardRule overrideCardRule, double sameModThreshold = 0.5)
        : this(DefaultMinimumContainedResourceCount, overrideCardRule, sameModThreshold) { }
}

public sealed record PackageConflictGroupingFile(string Path, FileStamp ScannedStamp, int ResourceCount);

public sealed record PackageConflictGroupingInputIssue(string Code, string Message, ResourceKey? Key = null);

/// <summary>保留原始结构化原因；退出不属于文件对关系。</summary>
public sealed record PackageConflictGroupingExcludedFile(
    string Path,
    IReadOnlyList<PackageConflictScanIssue> ScanIssues,
    IReadOnlyList<ResourceContentOccurrence> FailedResources,
    IReadOnlyList<ResourceContentPackageVersion> InvalidVersions,
    IReadOnlyList<PackageConflictGroupingInputIssue> InputIssues);

public sealed record PackageConflictGroupingInput(
    bool ExcludeNameMap,
    IReadOnlyList<PackageConflictGroupingFile> Files,
    IReadOnlyList<PackageConflictPairSummary> Pairs,
    IReadOnlyList<ResourceContentComparison> Comparisons,
    IReadOnlyList<PackageConflictGroupingExcludedFile> ExcludedFiles)
{
    public int FileCount => Files.Count;
}

public sealed record PackageConflictPairRelation(
    PackageConflictPairSummary Pair,
    PackageConflictRelationKind Kind,
    double Overlap,
    string? ContainedPath = null,
    string? BundlePath = null)
{
    public int SharedResourceCount => Pair.Resources.Count;
    public bool HasDifferentContent => Pair.HasConfirmedDifference;
    public bool HasIdenticalContent => Pair.HasConfirmedIdentical;
}

public sealed record PackageConflictGroupMember(
    string Path, FileStamp ScannedStamp, int ResourceCount, int SharedResourceCount, int UniqueResourceCount,
    bool IsBundle, bool? ContainedContentIdentical);

/// <param name="ResourceKeys">覆盖卡为本卡覆盖关系的圈卡依据键；合集、疑似卡为卡内全部撞号键。</param>
public sealed record PackageConflictGroup(
    PackageConflictRelationKind Kind,
    IReadOnlyList<PackageConflictGroupMember> Members,
    IReadOnlyList<ResourceKey> ResourceKeys,
    IReadOnlyList<PackageConflictPairRelation> Relations,
    string? BundlePath = null,
    int? BundleUniqueResourceCount = null);

public sealed record PackageConflictGroupingResult(
    IReadOnlyList<PackageConflictPairRelation> Relations,
    IReadOnlyList<PackageConflictGroup> Groups,
    IReadOnlyList<PackageConflictGroupingExcludedFile> ExcludedFiles)
{
    public IReadOnlyDictionary<string, IReadOnlyList<PackageConflictGroup>> GroupsByFile { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<PackageConflictGroup>>(
            new Dictionary<string, IReadOnlyList<PackageConflictGroup>>(PathRules.Comparer));
}
