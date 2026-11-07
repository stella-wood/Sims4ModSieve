using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class ConflictResultSession
{
    private static string ScriptGroupIdentity(string topLevelName, IEnumerable<string> paths) => topLevelName + "/"
        + string.Concat(paths.Select(path => path.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Identity));

    private static ConflictPairPriority ScriptPriority(IReadOnlyList<ScriptModuleCollision> modules) =>
        modules.Any(module => module.HasConfirmedDifference) ? ConflictPairPriority.NeedsReview
            : modules.Any(module => !module.IsComplete || module.ContentGroups.Count != 1) ? ConflictPairPriority.Incomplete
            : ConflictPairPriority.CanOrganize;

    private static string ScriptExplanation(IReadOnlyList<ScriptModuleCollision> modules)
    {
        var different = modules.Count(module => module.HasConfirmedDifference);
        var incomplete = modules.Count(module => !module.IsComplete || module.ContentGroups.Count == 0);
        return $"都带了 {modules[0].TopLevelName}，共 {modules.Count} 个模块，"
            + (different == 0 && incomplete == 0 ? "内容都相同"
                : $"其中 {different} 个内容不同、{incomplete} 个没比完。游戏对同名模块只会加载一份");
    }

    private (string Text, string Tooltip) ScriptFileAnnotation(ConflictCard card, ConflictFile file)
    {
        var different = 0;
        var identical = 0;
        var incomplete = 0;
        foreach (var module in card.Modules!)
        {
            var ownVersion = module.ContentGroups.FirstOrDefault(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path)));
            if (ownVersion is not null && module.ContentGroups.Any(group => !ReferenceEquals(group, ownVersion))) different++;
            if (ownVersion is not null && module.IsComplete && module.ContentGroups.Count == 1) identical++;
            if (!module.IsComplete || ownVersion is null) incomplete++;
        }
        var inventories = card.Files.Select(member => (File: member, Inventory: scriptInventoriesByPath.GetValueOrDefault(member.Path))).ToArray();
        int? unique = null;
        if (inventories.All(item => item.Inventory is not null && item.Inventory.ScannedStamp == item.File.Stamp))
        {
            var own = inventories.Single(item => StringComparer.OrdinalIgnoreCase.Equals(item.File.Path, file.Path)).Inventory!;
            var others = inventories.Where(item => !StringComparer.OrdinalIgnoreCase.Equals(item.File.Path, file.Path))
                .SelectMany(item => item.Inventory!.ModuleNames).ToHashSet(StringComparer.Ordinal);
            unique = own.ModuleNames.Count(name => !others.Contains(name));
        }
        return ($"撞 {different} 样 · " + (unique is { } count ? $"独有 {count} 样" : "独有数量未知"),
            $"本组同名模块 {card.Modules.Count} 个，其中 {identical} 个内容相同、{incomplete} 个未分析；"
            + (unique is { } known ? $"整份文件独有模块 {known} 个" : "缺少完整模块清单，独有数量未知"));
    }
    private PackageConflictGroupingInput? groupingInput;
    private DuplicateReport? duplicateReport;
    private IReadOnlySet<string> hashChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<ResourceKey, ResourceContentComparison> comparisonsByKey = new Dictionary<ResourceKey, ResourceContentComparison>();
    private IReadOnlyDictionary<string, ScriptArchiveInventory> scriptInventoriesByPath = new Dictionary<string, ScriptArchiveInventory>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ConflictProblemRow> originalProblems = [];
    private IReadOnlyList<PackageConflictPairSummary> originalResources = [];
    private IReadOnlyList<ScriptConflictPairSummary> originalScripts = [];
    private IReadOnlyList<ConflictCard> originalCards = [];
    private readonly HashSet<string> removedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ignoredGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignoredFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> groupNumbers = new(StringComparer.Ordinal);
    public IReadOnlyList<ConflictCard> Cards { get; private set; } = [];
    public PackageConflictGroupingResult? Grouping { get; private set; }
    public int DuplicateGroupCount => Cards.Count(card => card.Duplicate is not null);

    private void ResetGroups()
    {
        groupingInput = null;
        duplicateReport = null;
        hashChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        comparisonsByKey = new Dictionary<ResourceKey, ResourceContentComparison>();
        scriptInventoriesByPath = new Dictionary<string, ScriptArchiveInventory>(StringComparer.OrdinalIgnoreCase);
        originalProblems = [];
        originalResources = [];
        originalScripts = [];
        originalCards = [];
        Grouping = null;
        Cards = [];
        removedPaths.Clear();
        ignoredGroups.Clear();
        groupNumbers.Clear();
    }

    private void InitializeGroups(PackageConflictGroupingInput input, PackageConflictGroupingResult result,
        DuplicateReport? duplicates, IReadOnlySet<string> changedPaths, CancellationToken token)
    {
        groupingInput = input;
        duplicateReport = duplicates;
        hashChangedPaths = changedPaths;
        comparisonsByKey = ContentReport!.Comparisons.GroupBy(comparison => comparison.Key).ToDictionary(group => group.Key, group => group.First());
        scriptInventoriesByPath = ScriptReport!.ArchiveInventories.ToDictionary(inventory => inventory.Path, StringComparer.OrdinalIgnoreCase);
        originalProblems = Problems;
        originalResources = Resources;
        originalScripts = Scripts;
        RebuildCards(result, token);
        originalCards = Cards;
    }

    public void ToggleIgnored(ConflictResultRow row)
    {
        var ignoreWholeCard = row.File is null;
        var set = ignoreWholeCard ? ignoredGroups : ignoredFiles;
        var groupKey = row.IsCardStart ? row.Key : row.OwnerKey;
        var key = ignoreWholeCard ? groupKey : Path.GetFullPath(row.File!.Path);
        if (!set.Remove(key)) set.Add(key);
    }

    public void SetIgnoredFiles(IEnumerable<string> paths)
    {
        ignoredFiles.Clear();
        ignoredFiles.UnionWith(paths.Select(Path.GetFullPath));
    }

    public bool IsFileIgnored(string path) => ignoredFiles.Contains(Path.GetFullPath(path));

    public bool IsGroupIgnored(string key) => ignoredGroups.Contains(key);

    // 后台投影只读此快照；UI 的忽略、展开或删除不会修改正在读取的集合。
    public ConflictResultSession ProjectionSnapshot()
    {
        var snapshot = new ConflictResultSession
        {
            Resources = Resources, Scripts = Scripts, Problems = Problems, RangeProblems = RangeProblems,
            PackageReport = PackageReport, ContentReport = ContentReport, ScriptReport = ScriptReport,
            Cards = Cards, Grouping = Grouping,
            comparisonsByKey = comparisonsByKey,
            scriptInventoriesByPath = scriptInventoriesByPath,
            groupingInput = groupingInput, duplicateReport = duplicateReport,
            hashChangedPaths = hashChangedPaths,
            originalProblems = originalProblems, originalResources = originalResources, originalScripts = originalScripts,
            originalCards = originalCards,
        };
        snapshot.ignoredGroups.UnionWith(ignoredGroups);
        snapshot.ignoredFiles.UnionWith(ignoredFiles);
        snapshot.removedPaths.UnionWith(removedPaths);
        foreach (var entry in groupNumbers) snapshot.groupNumbers.Add(entry.Key, entry.Value);
        return snapshot;
    }

    public bool MatchesOriginalFile(string path, long size, DateTime? lastWriteUtc) => lastWriteUtc is not null
        && (originalCards.SelectMany(card => card.Files).Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, path)
            && file.Stamp is { } stamp && stamp.Length == size && stamp.LastWriteTimeUtc == lastWriteUtc)
            || PackageReport?.ResourceInventories.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, path)
                && item.ScannedStamp.Length == size && item.ScannedStamp.LastWriteTimeUtc == lastWriteUtc) == true
            || originalProblems.Any(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, path)
                && problem.Stamp is { } stamp && stamp.Length == size && stamp.LastWriteTimeUtc == lastWriteUtc));

    public void ApplyDeletedPaths(IEnumerable<string> paths)
    {
        removedPaths.UnionWith(paths);
        RebuildActiveGroups();
    }

    public void ApplyRestoredPaths(IEnumerable<string> paths)
    {
        removedPaths.ExceptWith(paths);
        RebuildActiveGroups();
    }

    public void AcceptFileChanges(ConflictResultSession prepared)
    {
        removedPaths.Clear();
        removedPaths.UnionWith(prepared.removedPaths);
        foreach (var entry in prepared.groupNumbers) groupNumbers[entry.Key] = entry.Value;
        Cards = prepared.Cards;
        Grouping = prepared.Grouping;
        Problems = prepared.Problems;
        Resources = prepared.Resources;
        Scripts = prepared.Scripts;
    }

    private void RebuildActiveGroups()
    {
        if (groupingInput is null) return;
        Problems = originalProblems.Where(problem => !removedPaths.Contains(problem.Path)).ToArray();
        Resources = originalResources.Where(pair => !removedPaths.Contains(pair.FirstPath) && !removedPaths.Contains(pair.SecondPath)).ToArray();
        Scripts = originalScripts.Where(pair => !removedPaths.Contains(pair.FirstPath) && !removedPaths.Contains(pair.SecondPath)).ToArray();
        var active = groupingInput with
        {
            Files = groupingInput.Files.Where(file => !removedPaths.Contains(file.Path)).ToArray(),
            Pairs = groupingInput.Pairs.Where(pair => !removedPaths.Contains(pair.FirstPath) && !removedPaths.Contains(pair.SecondPath)).ToArray(),
        };
        var rebuilt = new PackageConflictGroupingBuilder().Build(active, new());
        // 删除只更新当前排查卡的现存成员；正式分组资格不应让用户正在处理的卡消失。
        Cards = originalCards.Select(card =>
        {
            var files = card.Files.Where(file => !removedPaths.Contains(file.Path)).ToArray();
            if (files.Length == card.Files.Count) return card;
            var package = card.Package;
            if (package is not null)
            {
                var relations = package.Relations.Where(relation => !removedPaths.Contains(relation.Pair.FirstPath)
                    && !removedPaths.Contains(relation.Pair.SecondPath)).ToArray();
                var paths = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var allPairs = active.Pairs.Where(pair => paths.Contains(pair.FirstPath) && paths.Contains(pair.SecondPath)).ToArray();
                var displayPairs = package.Kind == PackageConflictRelationKind.OverrideConflict
                    ? relations.Select(relation => relation.Pair).ToArray() : allPairs;
                var members = package.Members.Where(member => paths.Contains(member.Path)).Select(member =>
                {
                    var shared = allPairs.Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, member.Path)
                            || StringComparer.OrdinalIgnoreCase.Equals(pair.SecondPath, member.Path))
                        .SelectMany(pair => pair.Resources).Select(match => match.Key).Distinct().Count();
                    var displayShared = displayPairs.Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, member.Path)
                            || StringComparer.OrdinalIgnoreCase.Equals(pair.SecondPath, member.Path))
                        .SelectMany(pair => pair.Resources).Select(match => match.Key).Distinct().Count();
                    var containment = relations.FirstOrDefault(relation => StringComparer.OrdinalIgnoreCase.Equals(relation.ContainedPath, member.Path));
                    return member with
                    {
                        SharedResourceCount = displayShared, UniqueResourceCount = member.ResourceCount - shared,
                        IsBundle = relations.Any(relation => StringComparer.OrdinalIgnoreCase.Equals(relation.BundlePath, member.Path)),
                        ContainedContentIdentical = containment is null ? null : !containment.HasDifferentContent,
                    };
                }).ToArray();
                var bundle = members.FirstOrDefault(member => member.IsBundle);
                package = package with
                {
                    Members = members,
                    Relations = relations,
                    ResourceKeys = displayPairs.SelectMany(pair => pair.Resources).Select(match => match.Key).Distinct().ToArray(),
                    BundlePath = bundle?.Path,
                    BundleUniqueResourceCount = bundle is null ? null : bundle.ResourceCount
                        - relations.SelectMany(relation => relation.Pair.Resources).Select(match => match.Key).Distinct().Count(),
                };
            }
            var duplicate = card.Duplicate;
            if (duplicate is not null && files.Length > 0)
            {
                var copies = duplicate.Files.Where(file => !removedPaths.Contains(file.Path)).ToArray();
                var selection = copies.Length > 1 ? new DuplicateSelectionService().Select(copies)
                    : (KeepPath: copies[0].Path, DeletePaths: (IReadOnlyList<string>)Array.Empty<string>());
                duplicate = duplicate with { Files = copies, SuggestedKeepPath = selection.KeepPath, SuggestedDeletePaths = selection.DeletePaths };
            }
            var modules = card.Modules?.Select(originalModule => originalModule with
            {
                ContentGroups = originalModule.ContentGroups.Select(group => group with
                {
                    Occurrences = group.Occurrences.Where(item => !removedPaths.Contains(item.ArchivePath)).ToArray(),
                }).Where(group => group.Occurrences.Count > 0).ToArray(),
                Uncompared = originalModule.Uncompared.Where(item => !removedPaths.Contains(item.ArchivePath)).ToArray(),
            }).ToArray();
            return card with
            {
                Files = files, Package = package, Duplicate = duplicate, Modules = modules, HasRemovedFiles = true,
                Priority = modules is null ? card.Priority : ScriptPriority(modules),
                Type = modules is null ? card.Type : ScriptPriority(modules) == ConflictPairPriority.CanOrganize ? "script-duplicate" : "script-conflict",
                Title = modules is null ? card.Title : ScriptPriority(modules) == ConflictPairPriority.CanOrganize ? "重复文件（脚本）" : "脚本冲突",
                Explanation = $"已移除 {card.Files.Count - files.Length} 个文件，剩余 {files.Length} 个文件",
            };
        }).Where(card => card.Files.Count > 0).ToArray();
        Cards = Cards.Where(card => card.Modules is null).Concat(Cards.Where(card => card.Modules is not null)
            .GroupBy(card => ScriptGroupIdentity(card.Modules![0].TopLevelName, card.Files.Select(file => file.Path)), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.OrderBy(card => ignoredGroups.Contains(card.Key)).ThenBy(card => card.GroupIndex).First();
                if (group.Count() == 1) return first;
                var modules = group.SelectMany(card => card.Modules!).DistinctBy(module => module.ModuleName, StringComparer.Ordinal)
                    .OrderBy(module => module.ModuleName, StringComparer.Ordinal).ToArray();
                var priority = ScriptPriority(modules);
                return first with
                {
                    Modules = modules, Priority = priority, HasRemovedFiles = true,
                    Type = priority == ConflictPairPriority.CanOrganize ? "script-duplicate" : "script-conflict",
                    Title = priority == ConflictPairPriority.CanOrganize ? "重复文件（脚本）" : "脚本冲突",
                    Explanation = group.First(card => card.HasRemovedFiles).Explanation,
                };
            })).OrderBy(card => card.GroupIndex).ToArray();
        var groups = Cards.Where(card => card.Package is not null).Select(card => card.Package!).ToArray();
        Grouping = rebuilt with
        {
            Groups = groups,
            GroupsByFile = groups.SelectMany(group => group.Members.Select(member => (member.Path, Group: group)))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PackageConflictGroup>)group.Select(item => item.Group).ToArray(), StringComparer.OrdinalIgnoreCase),
        };
    }

    private void RebuildCards(PackageConflictGroupingResult result, CancellationToken token)
    {
        var representatives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packagePaths = groupingInput!.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in duplicateReport?.Groups ?? [])
        {
            var eligible = duplicate.Files.Where(file => packagePaths.Contains(file.Path) && !hashChangedPaths.Contains(file.Path)
                && MatchesScannedStamp(file, PackageReport, ScriptReport)).ToArray();
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
        result = result with
        {
            Groups = packageGroups,
            GroupsByFile = packageGroups.SelectMany(group => group.Members.Select(member => (member.Path, Group: group)))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<PackageConflictGroup>)group.Select(item => item.Group).ToArray(), StringComparer.OrdinalIgnoreCase),
        };
        Grouping = result;
        var cards = new List<ConflictCard>();
        foreach (var group in result.Groups)
        {
            token.ThrowIfCancellationRequested();
            var (type, title) = group.Kind switch
            {
                PackageConflictRelationKind.OverrideConflict => ("override", "资源覆盖冲突"),
                PackageConflictRelationKind.BundleContainment => ("collection", "合集包含"),
                _ => ("suspected", "疑似同一 mod"),
            };
            var files = group.Members.OrderByDescending(member => member.IsBundle)
                .ThenByDescending(member => type == "suspected" ? member.ScannedStamp.LastWriteTimeUtc : DateTime.MinValue)
                .ThenBy(member => member.Path, StringComparer.OrdinalIgnoreCase).Select(member => new ConflictFile(member.Path, member.ScannedStamp)).ToArray();
            var key = type + "/" + ConflictIgnoreKeyBuilder.Build(files.Select(file => RelativeIdentity(file.Path)), group.ResourceKeys);
            var different = group.Relations.SelectMany(relation => relation.Pair.Resources)
                .Where(match => match.HasConfirmedDifference).Select(match => match.Key).Distinct().Count();
            var explanation = type switch
            {
                "override" => $"有 {group.ResourceKeys.Count} 项资源编号相同，其中 {different} 项内容不同；游戏对每个编号只会使用一份资源",
                "collection" => $"合集里包含{group.Members.Where(member => !member.IsBundle).Select(member => Representative(member.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}个另外单独放着的文件，"
                    + $"{group.Members.Where(member => member.ContainedContentIdentical == true).Select(member => Representative(member.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}个已被包含，"
                    + $"{group.Members.Where(member => member.ContainedContentIdentical == false).Select(member => Representative(member.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}个存在不同。还有{group.BundleUniqueResourceCount}样资源只在合集里有。",
                _ => $"有 {group.ResourceKeys.Count} 项资源编号相同，其中 {different} 项内容不同，可能是同一 mod 的不同版本",
            };
            cards.Add(new(key, type, title, type == "override" ? ConflictPairPriority.NeedsReview : ConflictPairPriority.CanOrganize,
                files, explanation, Package: group));
        }
        var modules = (ScriptReport?.Collisions ?? [])
            .Select(module => module with
            {
                ContentGroups = module.ContentGroups.Select(group => group with
                {
                    Occurrences = group.Occurrences.Where(item => !removedPaths.Contains(item.ArchivePath)
                        && !hashChangedPaths.Contains(item.ArchivePath)).ToArray(),
                }).Where(group => group.Occurrences.Count > 0).ToArray(),
                Uncompared = module.Uncompared.Where(item => !removedPaths.Contains(item.ArchivePath)
                    && !hashChangedPaths.Contains(item.ArchivePath)).ToArray(),
            }).Where(module => module.ArchiveCount >= 2);
        foreach (var group in modules.GroupBy(module => ScriptGroupIdentity(module.TopLevelName,
            module.Occurrences.Select(item => item.ArchivePath)), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var collisions = group.OrderBy(module => module.ModuleName, StringComparer.Ordinal).ToArray();
            var files = collisions[0].Occurrences.DistinctBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ConflictFile(item.ArchivePath, item.ArchiveStamp)).ToArray();
            var priority = ScriptPriority(collisions);
            cards.Add(new("script/" + Identity(group.Key), priority == ConflictPairPriority.CanOrganize ? "script-duplicate" : "script-conflict",
                priority == ConflictPairPriority.CanOrganize ? "重复文件（脚本）" : "脚本冲突", priority, files,
                ScriptExplanation(collisions), Modules: collisions));
        }
        foreach (var source in duplicateReport?.Groups ?? [])
        {
            token.ThrowIfCancellationRequested();
            var files = source.Files.Where(file => !removedPaths.Contains(file.Path) && !hashChangedPaths.Contains(file.Path)
                && MatchesScannedStamp(file, PackageReport, ScriptReport)
                && !Problems.Any(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, file.Path))).ToArray();
            if (files.Length < 2) continue;
            var selection = new DuplicateSelectionService().Select(files);
            var duplicate = source with { Files = files, SuggestedKeepPath = selection.KeepPath, SuggestedDeletePaths = selection.DeletePaths };
            cards.Add(new("duplicate/" + source.FileSize + "/" + source.Sha256, "duplicates", "重复文件", ConflictPairPriority.CanOrganize,
                files.OrderByDescending(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, selection.KeepPath)).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(file => new ConflictFile(file.Path, new(file.Size, file.LastWriteTimeUtc, file.Identity))).ToArray(), "", Duplicate: duplicate));
        }
        var duplicateCards = cards.Where(card => card.Duplicate is not null).ToArray();
        cards.RemoveAll(card => card.Package?.Kind == PackageConflictRelationKind.SuspectedSameMod
            && duplicateCards.Any(duplicate => card.Files.All(file => duplicate.Files.Any(copy =>
                StringComparer.OrdinalIgnoreCase.Equals(copy.Path, file.Path)))));
        var ordered = cards.OrderBy(card => card.Priority).ThenBy(card => card.Type == "duplicates" ? 0 : 1)
            .ThenByDescending(card => card.Files.Count).ThenBy(card => card.Key, StringComparer.Ordinal).ToArray();
        foreach (var card in ordered)
            if (!groupNumbers.ContainsKey(card.Key)) groupNumbers.Add(card.Key, groupNumbers.Count + 1);
        Cards = ordered.Select(card => card with { GroupIndex = groupNumbers[card.Key] }).ToArray();
        foreach (var key in Problems.Select(problem => "problem" + Identity(problem.Path.ToUpperInvariant()))
            .Concat(RangeProblems.Select((problem, index) => "range" + Identity(problem.Path.ToUpperInvariant()) + index)))
            if (!groupNumbers.ContainsKey(key)) groupNumbers.Add(key, groupNumbers.Count + 1);
    }

    private void AddGroupCards(List<(ConflictPairPriority Priority, string Key, bool Included, Action Build)> cards,
        Func<ConflictResultRow, bool> add, string view, IReadOnlySet<string> expanded, bool showIgnored,
        IReadOnlySet<string>? types, CancellationToken token)
    {
        foreach (var card in Cards)
        {
            token.ThrowIfCancellationRequested();
            var ignored = ignoredGroups.Contains(card.Key);
            var included = (showIgnored || !ignored) && (types is null || types.Contains(card.Type))
                && (view == "all" || view == "resources" && card.Package is not null
                    || view == "scripts" && card.Modules is not null || view == "duplicates" && card.Duplicate is not null);
            cards.Add((card.Priority, card.Key, included, () =>
            {
                var ignoredCount = card.Files.Count(file => IsFileIgnored(file.Path));
                var isDuplicateGroup = card.Duplicate is not null || card.Type == "script-duplicate";
                var paths = card.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var copyCount = isDuplicateGroup ? 0
                    : Cards.Where(other => other.Duplicate is not null)
                        .Sum(other => Math.Max(0, other.Files.Count(file => paths.Contains(file.Path)) - 1));
                var counts = new List<string>();
                if (copyCount > 0) counts.Add($"{copyCount} 份重复");
                if (ignoredCount > 0) counts.Add($"{ignoredCount} 已忽略");
                var summary = $"这 {card.Files.Count} 份文件" + (counts.Count > 0 ? " (含 " + string.Join("、", counts) + ")" : "");
                var kind = card.Package is not null ? ConflictRowKind.Resource : card.Modules is not null ? ConflictRowKind.Module : ConflictRowKind.Duplicate;
                if (!add(new(card.Key, kind, 0, card.Title, Priority: card.Priority, IsExpandable: true,
                    FileCountText: $"{card.Files.Count} 文件" + (ignoredCount > 0 ? $" · {ignoredCount} 已忽略" : ""), IsIgnored: ignored, OwnerKey: card.Key))) return;
                var separator = card.Modules is not null || card.Duplicate is not null && !card.HasRemovedFiles
                    || !card.HasRemovedFiles && card.Package?.Kind is PackageConflictRelationKind.OverrideConflict or PackageConflictRelationKind.SuspectedSameMod ? counts.Count > 0 ? " " : "" : "；";
                add(new(card.Key + "/explanation", ConflictRowKind.Explanation, 1,
                    summary + separator + (card.Modules is not null ? ScriptExplanation(card.Modules) + (card.HasRemovedFiles ? "。" + card.Explanation : "")
                        : card.Duplicate is null || card.HasRemovedFiles ? card.Explanation : "内容完全相同。"),
                    OwnerKey: card.Key, IsIgnored: ignored, DuplicateLink: card.Duplicate is null || card.HasRemovedFiles ? null : Link(card)));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var activeFiles = card.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var annotationPairs = card.Package is null ? null : card.HasRemovedFiles && card.Package.Kind != PackageConflictRelationKind.OverrideConflict
                    ? groupingInput!.Pairs.Where(pair => activeFiles.Contains(pair.FirstPath) && activeFiles.Contains(pair.SecondPath)).ToArray()
                    : card.Package.Relations.Select(relation => relation.Pair).ToArray();
                var visibleIndex = 0;
                foreach (var file in card.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(file.Path)) continue;
                    var duplicate = isDuplicateGroup ? null : DuplicateFor(file.Path);
                    var siblings = duplicate?.Files.Where(copy => card.Files.Any(member => StringComparer.OrdinalIgnoreCase.Equals(member.Path, copy.Path))).ToArray() ?? [];
                    var visibleSiblings = siblings.Where(copy => !IsFileIgnored(copy.Path)).ToArray();
                    var representative = visibleSiblings.FirstOrDefault(copy => StringComparer.OrdinalIgnoreCase.Equals(copy.Path, duplicate!.Duplicate!.SuggestedKeepPath))
                        ?? visibleSiblings.FirstOrDefault()
                        ?? siblings.FirstOrDefault(copy => StringComparer.OrdinalIgnoreCase.Equals(copy.Path, duplicate!.Duplicate!.SuggestedKeepPath))
                        ?? siblings.FirstOrDefault() ?? file;
                    seen.UnionWith(siblings.Select(copy => copy.Path));
                    var key = FileKey(card.Key, representative.Path);
                    var fileIgnored = IsFileIgnored(representative.Path);
                    if (!showIgnored && fileIgnored) continue;
                    var member = card.Package?.Members.First(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, representative.Path));
                    var sharedResources = annotationPairs?.Where(pair =>
                            StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, representative.Path)
                            || StringComparer.OrdinalIgnoreCase.Equals(pair.SecondPath, representative.Path))
                        .SelectMany(pair => pair.Resources).GroupBy(match => match.Key).ToArray();
                    var differentCount = sharedResources?.Count(matches => matches.Any(match => match.HasConfirmedDifference)) ?? 0;
                    var annotation = member is null ? card.Duplicate is not null && visibleIndex > 0 ? "内容完全相同" : ""
                        : member.IsBundle ? $"独有 {member.UniqueResourceCount} 样"
                        : member.ContainedContentIdentical is { } identical ? identical ? "已被包含" : "存在不同"
                        : $"撞 {differentCount} 样 · 独有 {member.UniqueResourceCount} 样";
                    var annotationTooltip = member is not null && !member.IsBundle && member.ContainedContentIdentical is null
                        ? $"相同TGI编号{sharedResources!.Length}个，其中{sharedResources.Count(matches => matches.All(match => match.HasConfirmedIdentical && !match.HasConfirmedDifference))}个内容相同，独有编号{member.UniqueResourceCount}个"
                        : null;
                    if (card.Modules is not null) (annotation, annotationTooltip) = ScriptFileAnnotation(card, representative);
                    var tag = member?.IsBundle == true ? "合集"
                        : card.Type == "suspected" && representative.Stamp?.LastWriteTimeUtc == card.Files.Max(item => item.Stamp?.LastWriteTimeUtc) ? "最新"
                        : "";
                    add(new(key, ConflictRowKind.File, 1, representative.Name, representative.Path + "\n" + representative.Metadata,
                        Tooltip: representative.Path, OwnerKey: card.Key, File: representative, Annotation: annotation, Tag: tag,
                        IsIgnored: ignored || fileIgnored, IsBundle: member?.IsBundle == true,
                        DuplicateLink: duplicate is null ? null : Link(duplicate), CopyCount: duplicate is null ? 0 : duplicate.Files.Count - 1,
                        AnnotationTooltip: annotationTooltip));
                    visibleIndex++;
                }
                if (card.Duplicate is not null) return;
                if (card.Files.Count < 2 || card.Package is { ResourceKeys.Count: 0 }) return;
                var technicalKey = card.Key + "/technical";
                if (!add(new(technicalKey, ConflictRowKind.Technical, 1, TechnicalDetailTitle, IsExpandable: true,
                    OwnerKey: card.Key, IsIgnored: ignored))) return;
                var references = Cards.Where(other => other.Key != card.Key && other.ResourceKeys.Intersect(card.ResourceKeys).Any()).OrderBy(other => other.GroupIndex).ToArray();
                var cardKeys = card.ResourceKeys.ToHashSet();
                var relatedByTgi = new Dictionary<string, List<ConflictGroupLink>>(StringComparer.Ordinal);
                foreach (var reference in references)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var key in reference.ResourceKeys.Where(cardKeys.Contains))
                    {
                        if (!relatedByTgi.TryGetValue(key.Tgi, out var links)) relatedByTgi.Add(key.Tgi, links = []);
                        links.Add(Link(reference));
                    }
                }
                var entries = TechnicalEntries(card).Select(entry => entry with
                {
                    RelatedGroups = relatedByTgi.TryGetValue(entry.Identifier, out var links) ? links.ToArray() : []
                }).OrderByDescending(entry => entry.HasDifferentVersions).ToArray();
                var copyText = card.Title + " · 第 " + card.GroupIndex + " 组\n" + string.Join("\n\n", entries.Select(entry => entry.CopyText))
                    + string.Concat(references.Select(other => $"\n相关组：第 {other.GroupIndex} 组"));
                var index = 0;
                if (entries.Length <= 5 && card.Files.Count >= 15)
                {
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        add(new(technicalKey + "/listheader" + index++, ConflictRowKind.TechnicalListHeader, 1, entry.Identifier,
                            OwnerKey: card.Key, TechnicalEntry: entry, IsIgnored: ignored));
                        foreach (var cell in entry.Cells)
                        {
                            token.ThrowIfCancellationRequested();
                            add(new(technicalKey + "/listfile" + index++, ConflictRowKind.TechnicalListFile, 1, cell.FileName,
                                OwnerKey: card.Key, TechnicalEntry: entry with { Cells = [cell] }, IsIgnored: ignored));
                        }
                    }
                }
                else
                {
                    var table = new ConflictTechnicalTableState(card.Files.Count, entries.Any(entry => entry.RelatedGroups.Count > 0));
                    ConflictTechnicalCell[] headers = [new("种类", "", "", table) { IsType = true, IsHeader = true },
                        .. card.Files.Select(file => new ConflictTechnicalCell(file.Name, "", file.Path, table) { IsHeader = true }),
                        .. table.HasRelatedGroups ? new ConflictTechnicalCell[] { new("相关组", "", "", table) { IsRelated = true, IsHeader = true } } : []];
                    var headerRow = new ConflictResultRow(technicalKey + "/header", ConflictRowKind.TechnicalHeader, 1, "TGI／模块", OwnerKey: card.Key,
                        IsIgnored: ignored, TechnicalEntry: new("TGI／模块", "种类", headers), TechnicalTable: table);
                    table.HeaderRow = headerRow;
                    add(headerRow);
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        add(new(technicalKey + "/value" + index++, ConflictRowKind.Technical, 1, entry.Identifier,
                            OwnerKey: card.Key, TechnicalEntry: entry with { Cells = [new("", entry.TypeLabel, "", table) { IsType = true },
                                .. entry.Cells.Select(cell => cell with { FileName = "", Table = table }),
                                .. table.HasRelatedGroups ? new ConflictTechnicalCell[] { new("", "", "", table) { IsRelated = true, RelatedGroups = entry.RelatedGroups } } : []] },
                            IsIgnored: ignored, TechnicalTable: table));
                    }
                    add(new(technicalKey + "/scroll", ConflictRowKind.TechnicalScroll, 1, "横向滚动技术详情",
                        OwnerKey: card.Key, IsIgnored: ignored, TechnicalTable: table));
                }
                add(new(technicalKey + "/copy", ConflictRowKind.CopyDetails, 1, "复制详情", OwnerKey: card.Key,
                    CopyText: copyText, IsIgnored: ignored));
            }));
        }
    }

    private static bool MatchesScannedStamp(DuplicateFile file, PackageConflictScanReport? packages, ScriptModuleScanReport? scripts)
    {
        var stamp = new FileStamp(file.Size, file.LastWriteTimeUtc, file.Identity);
        var inventory = packages?.ResourceInventories.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, file.Path));
        if (inventory is not null && inventory.ScannedStamp != stamp) return false;
        var occurrences = scripts?.Collisions.SelectMany(collision => collision.Occurrences)
            .Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path));
        return occurrences is null || occurrences.All(item => item.ArchiveStamp == stamp);
    }

    // 会话内路径身份包含卷名，避免不同来源下同名相对路径碰撞。
    private static string RelativeIdentity(string path) => path.Replace(':', '_').TrimStart('\\', '/');
    private static string FileKey(string owner, string path) => owner + "/file" + Identity(path.ToUpperInvariant());
    private ConflictCard? DuplicateFor(string path) => Cards.FirstOrDefault(card => card.Duplicate is not null
        && card.Files.Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, path)));
    private static ConflictGroupLink Link(ConflictCard card) => new(card.Key, card.GroupIndex);

    public IReadOnlyList<ConflictTechnicalEntry> TechnicalEntries(ConflictCard card)
    {
        if (card.Package is { } package)
        {
            return package.ResourceKeys.Select(key =>
            {
                var comparison = comparisonsByKey[key];
                var versions = new HashSet<int>();
                var comparedFiles = 0;
                var incomplete = false;
                var cells = card.Files.Select(file =>
                {
                    var index = comparison.ContentGroups.ToList().FindIndex(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.PackagePath, file.Path)));
                    var group = index < 0 ? null : comparison.ContentGroups[index];
                    var failed = comparison.Uncompared.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.PackagePath, file.Path));
                    if (group is not null) { versions.Add(index); comparedFiles++; }
                    if (failed is not null) incomplete = true;
                    var version = group is not null ? $"版本 {index + 1}" : failed is not null ? $"未完成：{failed.Issue!.Message}" : "—";
                    var evidence = file.Path + "\n" + file.TechnicalMetadata;
                    if (group is not null) evidence += $"\nSHA256 {group.Sha256} · 资源解压大小 {group.DecompressedLength} 字节";
                    if (failed is not null) evidence += $"\n{failed.Issue!.Code} · {failed.Issue.Stage} · {failed.Issue.Detail ?? failed.Issue.Message}";
                    return new ConflictTechnicalCell(file.Name, version, evidence);
                }).ToArray();
                var identical = versions.Count == 1 && comparedFiles >= 2 && !incomplete;
                return new ConflictTechnicalEntry(key.Tgi, ResourceTypeName(key.Type),
                    cells.Select(cell => cell with { IsIdenticalVersion = identical }).ToArray())
                    { HasDifferentVersions = versions.Count > 1, HasIdenticalVersions = identical };
            }).ToArray();
        }
        if (card.Modules is { } modules)
            return modules.Select(module => new ConflictTechnicalEntry(module.ModuleName, "脚本模块", card.Files.Select(file =>
            {
                var index = module.ContentGroups.ToList().FindIndex(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path)));
                var occurrence = module.Occurrences.First(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path));
                var version = index >= 0 ? $"版本 {index + 1}" : occurrence.Issue is { } failure ? $"未完成：{failure.Message}" : "未完成";
                var evidence = file.Path + "\n" + file.TechnicalMetadata + "\n" + occurrence.EntryName;
                if (occurrence.IsSuccess) evidence += $"\nSHA256 {occurrence.Sha256} · 模块解压大小 {occurrence.ContentLength} 字节";
                if (occurrence.Issue is { } issue) evidence += $"\n{issue.Code} · {issue.Stage} · {issue.Detail ?? issue.Message}";
                return new ConflictTechnicalCell(file.Name, version, evidence);
            }).ToArray())
            { Conclusion = module.Verdict switch { ScriptModuleVerdict.Different => "内容不同", ScriptModuleVerdict.Identical => "内容相同", _ => "未分析" },
                HasDifferentVersions = module.HasConfirmedDifference, HasIdenticalVersions = module.Verdict == ScriptModuleVerdict.Identical }).ToArray();
        return [];
    }

    private static string ResourceTypeName(uint type)
    {
        var name = GetResourceTypeDisplayName(type);
        return name == "未识别资源" ? $"0x{type:X8}" : name;
    }

    public ConflictRelationshipsViewModel? Relationships(string path)
    {
        var own = Cards.Where(card => card.Files.Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, path))).ToArray();
        var main = own.SelectMany(card => card.Files).FirstOrDefault(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, path));
        if (main is null && Problems.FirstOrDefault(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, path)) is { } problem)
            main = new(path, problem.Stamp);
        if (main is null) return null;
        var related = new Dictionary<string, List<ConflictRelationship>>(StringComparer.OrdinalIgnoreCase);
        void Add(string partner, ConflictRelationship relationship)
        {
            if (!related.TryGetValue(partner, out var values)) related.Add(partner, values = []);
            if (!values.Contains(relationship)) values.Add(relationship);
        }
        // GroupsByFile 找卡，Relations 找直接文件对；同卡不代表逐对相关。
        var packageGroups = Grouping?.GroupsByFile.GetValueOrDefault(path) ?? [];
        foreach (var card in own.Where(card => card.Package is not null && packageGroups.Contains(card.Package)))
            foreach (var relation in card.Package!.Relations.Where(relation =>
                StringComparer.OrdinalIgnoreCase.Equals(relation.Pair.FirstPath, path) || StringComparer.OrdinalIgnoreCase.Equals(relation.Pair.SecondPath, path)))
            {
                var partner = StringComparer.OrdinalIgnoreCase.Equals(relation.Pair.FirstPath, path) ? relation.Pair.SecondPath : relation.Pair.FirstPath;
                Add(partner, new(card.Title, string.Join("、", relation.Pair.Resources.Select(match => ResourceTypeName(match.Key.Type)).Distinct())
                    + $" · {relation.SharedResourceCount} 样", Link(card),
                    ContainsMain: StringComparer.OrdinalIgnoreCase.Equals(relation.ContainedPath, path),
                    ContainedByMain: StringComparer.OrdinalIgnoreCase.Equals(relation.BundlePath, path)));
            }
        foreach (var card in own.Where(card => card.Modules is not null))
            foreach (var partner in card.Files.Where(file => !StringComparer.OrdinalIgnoreCase.Equals(file.Path, path)))
                Add(partner.Path,
                    new(card.Title, string.Join("、", card.Modules!.Select(module => module.ModuleName)), Link(card)));
        var duplicate = DuplicateFor(path);
        foreach (var file in duplicate?.Files ?? []) related.Remove(file.Path);
        var rows = new List<ConflictRelationshipRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var partner in related.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!visited.Add(partner)) continue;
            var copiesCard = DuplicateFor(partner);
            var siblings = copiesCard?.Files.Where(file => related.ContainsKey(file.Path)).ToArray() ?? [];
            var representative = siblings.FirstOrDefault(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, copiesCard!.Duplicate!.SuggestedKeepPath))
                ?? siblings.FirstOrDefault() ?? own.SelectMany(card => card.Files).First(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, partner));
            visited.UnionWith(siblings.Select(file => file.Path));
            var relations = (siblings.Length > 0 ? siblings.SelectMany(file => related[file.Path]) : related[partner]).Distinct().ToArray();
            var priority = relations.Any(item => Cards.Any(card => card.Key == item.Link.Key && card.Priority == ConflictPairPriority.NeedsReview))
                ? ConflictPairPriority.NeedsReview : ConflictPairPriority.CanOrganize;
            rows.Add(new(representative, priority, relations,
                copiesCard?.Files.Where(file => !StringComparer.OrdinalIgnoreCase.Equals(file.Path, representative.Path)).ToArray() ?? [], copiesCard is null ? null : Link(copiesCard)));
        }
        return new(main, own.Length, related.Values.SelectMany(value => value).Any(item => item.IsContainment),
            rows.OrderBy(row => row.Priority).ThenBy(row => row.File.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            duplicate?.Files.Where(file => !StringComparer.OrdinalIgnoreCase.Equals(file.Path, path)).ToArray() ?? [], duplicate is null ? null : Link(duplicate));
    }
}
