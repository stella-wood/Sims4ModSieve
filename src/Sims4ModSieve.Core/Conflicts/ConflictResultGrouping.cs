using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Conflicts;

public static class ConflictResultGrouping
{
    public static (PackageConflictGroupingInput Input, PackageConflictGroupingResult Result,
        IReadOnlySet<string> ChangedPaths, IReadOnlyList<DuplicateFile> StampMismatches) BuildPackages(
        PackageConflictScanReport packages, ResourceContentComparisonReport contents, ScriptModuleScanReport scripts,
        DuplicateReport? duplicates, CancellationToken cancellationToken = default,
        long? resourceMatchLimit = null,
        Action<ConflictGroupingSize>? sizeObserved = null)
    {
        var groupingBuilder = new PackageConflictGroupingBuilder(resourceMatchLimit);
        var prepared = groupingBuilder.Prepare(packages, contents, cancellationToken: cancellationToken,
            duplicates: duplicates, scripts: scripts, sizeObserved: sizeObserved);
        var stampMismatches = duplicates?.Groups.SelectMany(group => group.Files)
            .Where(file => !MatchesScannedStamp(file, packages, scripts))
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var changedPaths = (duplicates?.Issues ?? []).Where(issue => issue.Code == "file-changed-during-hash")
            .Select(issue => issue.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changedPaths.UnionWith(stampMismatches.Select(file => file.Path));
        // 只保留稳定文件，删除和撤回重建时不能重新使用旧版本的关系。
        prepared = prepared with
        {
            Files = prepared.Files.Where(file => !changedPaths.Contains(file.Path)).ToArray(),
            Pairs = prepared.Pairs.Where(pair => !changedPaths.Contains(pair.FirstPath) && !changedPaths.Contains(pair.SecondPath)).ToArray(),
        };
        var grouping = groupingBuilder.Build(prepared, new PackageConflictGroupingOptions(), cancellationToken);
        return (prepared, grouping, changedPaths, stampMismatches);
    }

    public static (PackageConflictGroupingResult Result, IReadOnlyDictionary<string, string> Representatives)
        MergeDuplicatePackageGroups(PackageConflictGroupingInput input,
        PackageConflictGroupingResult result, DuplicateReport? duplicates, IReadOnlySet<string> changedPaths,
        PackageConflictScanReport? packages, ScriptModuleScanReport? scripts, Action<string>? memoryObserved = null)
    {
        var representatives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packagePaths = input.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in duplicates?.Groups ?? [])
        {
            var eligible = duplicate.Files.Where(file => packagePaths.Contains(file.Path) && !changedPaths.Contains(file.Path)
                && MatchesScannedStamp(file, packages, scripts)).ToArray();
            if (eligible.Length < 2) continue;
            var keep = new DuplicateSelectionService().Select(eligible).KeepPath;
            foreach (var file in eligible) representatives[file.Path] = keep;
        }
        string Representative(string path) => representatives.GetValueOrDefault(path, path);
        var packageGroups = result.Groups.GroupBy(group => group.Kind + "/"
            + (group.BundlePath is null ? "" : Identity(Representative(group.BundlePath).ToUpperInvariant()))
            + string.Concat(group.Members.Select(member => Representative(member.Path).ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Identity))
            + string.Concat(group.ResourceKeys.Select(key => key.Tgi).Order(StringComparer.Ordinal).Select(Identity)), StringComparer.Ordinal)
            .Select(groups =>
            {
                var selected = groups.OrderByDescending(group => group.Members.Count(member =>
                    StringComparer.OrdinalIgnoreCase.Equals(member.Path, Representative(member.Path)))).First();
                return selected with
                {
                    Members = selected.Members.Concat(groups.SelectMany(group => group.Members)).DistinctBy(member => member.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Relations = groups.SelectMany(group => group.Relations).Distinct().ToArray(),
                };
            }).ToArray();
        var merged = result with
        {
            Groups = packageGroups,
            GroupsByFile = packageGroups.SelectMany(group => group.Members.Select(member => (member.Path, Group: group)))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PackageConflictGroup>)group.Select(item => item.Group).ToArray(), StringComparer.OrdinalIgnoreCase),
        };
        // 常规扫描不强制 GC；这个值包含尚未回收的临时对象，不能当作存活堆或阶段峰值。
        var memory = FormattableString.Invariant($"冲突分组内存：阶段=合并副本后；结束托管内存字节={GC.GetTotalMemory(false)}；强制GC=false");
        ScanPerformance.RecordDiagnostic(memory);
        memoryObserved?.Invoke(memory);
        return (merged, representatives);
    }

    public static IEnumerable<IGrouping<string, ScriptModuleCollision>> GroupScripts(
        IEnumerable<ScriptModuleCollision> collisions, Func<string, bool> exclude)
    {
        var modules = collisions.Select(module => module with
        {
            ContentGroups = module.ContentGroups.Select(group => group with
            {
                Occurrences = group.Occurrences.Where(item => !exclude(item.ArchivePath)).ToArray(),
            }).Where(group => group.Occurrences.Count > 0).ToArray(),
            Uncompared = module.Uncompared.Where(item => !exclude(item.ArchivePath)).ToArray(),
        }).Where(module => module.ArchiveCount >= 2);
        return modules.GroupBy(module => ScriptGroupIdentity(module.TopLevelName,
            module.Occurrences.Select(item => item.ArchivePath)), StringComparer.Ordinal);
    }

    public static ScriptModuleVerdict GetScriptVerdict(IReadOnlyList<ScriptModuleCollision> modules) =>
        modules.Any(module => module.HasConfirmedDifference) ? ScriptModuleVerdict.Different
            : modules.Any(module => module.Verdict == ScriptModuleVerdict.Incomplete) ? ScriptModuleVerdict.Incomplete
            : ScriptModuleVerdict.Identical;

    public static bool MatchesScannedStamp(DuplicateFile file, PackageConflictScanReport? packages, ScriptModuleScanReport? scripts)
    {
        var stamp = new FileStamp(file.Size, file.LastWriteTimeUtc, file.Identity);
        var inventory = packages?.ResourceInventories.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, file.Path));
        if (inventory is not null && inventory.ScannedStamp != stamp) return false;
        var occurrences = scripts?.Collisions.SelectMany(collision => collision.Occurrences)
            .Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path));
        return occurrences is null || occurrences.All(item => item.ArchiveStamp == stamp);
    }

    public static string ScriptGroupIdentity(string topLevelName, IEnumerable<string> paths) => topLevelName + "/"
        + string.Concat(paths.Select(path => path.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Identity));

    private static string Identity(string value) => $"/{value.Length}:{value}";
}
