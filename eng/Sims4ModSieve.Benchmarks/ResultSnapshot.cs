using System.Text.Json;
using System.Text.Json.Nodes;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Benchmarks;

internal static class ResultSnapshot
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static JsonObject Create(DuplicateReport duplicates, PackageConflictScanReport packages,
        ResourceContentComparisonReport contents, ScriptModuleScanReport scripts,
        PackageConflictGroupingResult grouping)
    {
        var builder = new ConflictPairSummaryBuilder();
        return (JsonObject)JsonSerializer.SerializeToNode(new
        {
            SchemaVersion = 1,
            Duplicates = new
            {
                duplicates.Sources, duplicates.DiscoveredFileCount, duplicates.HashedFileCount,
                duplicates.Groups, duplicates.Issues,
            },
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
        }, JsonOptions)!;
    }

    // Arrays are business order; object member order is serialization detail.
    internal static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is JsonObject left && actual is JsonObject right)
        {
            foreach (var key in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key)).Order(StringComparer.Ordinal))
            {
                if (!left.ContainsKey(key) || !right.ContainsKey(key)) return $"{path}.{key}: missing property";
                var difference = FirstDifference(left[key], right[key], $"{path}.{key}");
                if (difference is not null) return difference;
            }
            return null;
        }
        if (expected is JsonArray a && actual is JsonArray b)
        {
            if (a.Count != b.Count) return $"{path}: count {a.Count} != {b.Count}";
            for (var index = 0; index < a.Count; index++)
            {
                var difference = FirstDifference(a[index], b[index], $"{path}[{index}]");
                if (difference is not null) return difference;
            }
            return null;
        }
        return JsonNode.DeepEquals(expected, actual) ? null : $"{path}: value differs";
    }

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
