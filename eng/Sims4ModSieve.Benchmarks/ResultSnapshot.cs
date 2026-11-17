using System.Text.Json;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Benchmarks;

internal static partial class ResultSnapshot
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static object Create(DuplicateReport duplicates, PackageConflictScanReport packages,
        ResourceContentComparisonReport contents, ScriptModuleScanReport scripts,
        PackageConflictGroupingResult grouping)
    {
        var builder = new ConflictPairSummaryBuilder();
        return new
        {
            SchemaVersion = 1,
            Duplicates = DuplicateFacts(duplicates),
            Packages = new
            {
                packages.Sources, packages.DiscoveredPackageCount, packages.AnalyzedPackageCount,
                packages.IncompletePackagePaths, packages.Candidates, packages.Issues,
                packages.ResourceInventories,
                ToolingMetadataTypes = packages.ToolingMetadataTypes.Order().ToArray(),
            },
            Contents = new { contents.Comparisons, contents.Packages },
            Scripts = new
            {
                scripts.Sources, scripts.DiscoveredArchiveCount, scripts.AnalyzedArchiveCount,
                scripts.IncompleteArchivePaths, scripts.ModuleCount, scripts.Collisions, scripts.Issues,
                scripts.ArchiveInventories,
            },
            PackagePairs = builder.BuildPackagePairs(packages, contents).Select(Pair),
            ScriptPairs = builder.BuildScriptPairs(scripts).Select(pair => new
            {
                pair.FirstPath, pair.SecondPath, pair.DifferentModuleCount, pair.IdenticalModuleCount,
                pair.IncompleteModuleCount,
                Modules = pair.Modules.Select(match => new
                {
                    match.ModuleName, match.HasConfirmedDifference, match.HasConfirmedIdentical, match.HasIncomplete,
                }),
            }),
            Grouping = new
            {
                Relations = grouping.Relations.Select(Relation),
                Groups = grouping.Groups.Select(group => new
                {
                    group.Kind, group.Members, group.ResourceKeys, group.BundlePath, group.BundleUniqueResourceCount,
                    Relations = group.Relations.Select(Relation),
                }),
                grouping.ExcludedFiles,
            },
        };
    }

    internal static object CreateDuplicates(DuplicateReport duplicates) => new
    {
        SchemaVersion = 1,
        Duplicates = DuplicateFacts(duplicates),
    };

    private static object DuplicateFacts(DuplicateReport duplicates) => new
    {
        duplicates.Sources, duplicates.DiscoveredFileCount, duplicates.HashedFileCount,
        duplicates.Groups, duplicates.Issues,
    };

    private static object Pair(PackageConflictPairSummary pair) => new
    {
        pair.FirstPath, pair.SecondPath, pair.DifferentResourceCount, pair.IdenticalResourceCount,
        pair.IncompleteResourceCount, pair.FirstContainedInSecond, pair.SecondContainedInFirst,
        Resources = pair.Resources.Select(match => new
        {
            match.Key, match.HasConfirmedDifference, match.HasConfirmedIdentical, match.HasIncomplete,
        }),
    };

    private static object Relation(PackageConflictPairRelation relation) => new
    {
        Pair = Pair(relation.Pair), relation.Kind, relation.Overlap, relation.ContainedPath, relation.BundlePath,
    };
}
