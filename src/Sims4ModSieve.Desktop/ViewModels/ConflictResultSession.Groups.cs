using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class ConflictResultSession
{
    private PackageConflictGroupingInput? groupingInput;
    private DuplicateReport? duplicateReport;
    private IReadOnlySet<string> hashChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<ResourceKey, ResourceContentComparison> comparisonsByKey = new Dictionary<ResourceKey, ResourceContentComparison>();
    private IReadOnlyList<ConflictProblemRow> originalProblems = [];
    private IReadOnlyList<PackageConflictPairSummary> originalResources = [];
    private IReadOnlyList<ScriptConflictPairSummary> originalScripts = [];
    private readonly HashSet<string> removedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ignoredGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignoredFiles = new(StringComparer.Ordinal);
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
        originalProblems = [];
        originalResources = [];
        originalScripts = [];
        Grouping = null;
        Cards = [];
        removedPaths.Clear();
        ignoredGroups.Clear();
        ignoredFiles.Clear();
        groupNumbers.Clear();
    }

    private void InitializeGroups(PackageConflictGroupingInput input, PackageConflictGroupingResult result,
        DuplicateReport? duplicates, IReadOnlySet<string> changedPaths, CancellationToken token)
    {
        groupingInput = input;
        duplicateReport = duplicates;
        hashChangedPaths = changedPaths;
        comparisonsByKey = ContentReport!.Comparisons.GroupBy(comparison => comparison.Key).ToDictionary(group => group.Key, group => group.First());
        originalProblems = Problems;
        originalResources = Resources;
        originalScripts = Scripts;
        RebuildCards(result, token);
    }

    public void ToggleIgnored(ConflictResultRow row)
    {
        var set = row.File is null ? ignoredGroups : ignoredFiles;
        var key = row.File is null ? row.Key : FileKey(row.OwnerKey, row.File.Path);
        if (!set.Remove(key)) set.Add(key);
    }

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
            groupingInput = groupingInput, duplicateReport = duplicateReport,
            hashChangedPaths = hashChangedPaths,
            originalProblems = originalProblems, originalResources = originalResources, originalScripts = originalScripts,
        };
        snapshot.ignoredGroups.UnionWith(ignoredGroups);
        snapshot.ignoredFiles.UnionWith(ignoredFiles);
        snapshot.removedPaths.UnionWith(removedPaths);
        foreach (var entry in groupNumbers) snapshot.groupNumbers.Add(entry.Key, entry.Value);
        return snapshot;
    }

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
        RebuildCards(new PackageConflictGroupingBuilder().Build(active, new()), default);
    }

    private void RebuildCards(PackageConflictGroupingResult result, CancellationToken token)
    {
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
                .ThenBy(member => member.Path, StringComparer.OrdinalIgnoreCase).Select(member => new ConflictFile(member.Path, member.ScannedStamp)).ToArray();
            var key = type + "/" + ConflictIgnoreKeyBuilder.Build(files.Select(file => RelativeIdentity(file.Path)), group.ResourceKeys);
            var different = group.Relations.SelectMany(relation => relation.Pair.Resources)
                .Where(match => match.HasConfirmedDifference).Select(match => match.Key).Distinct().Count();
            var explanation = type switch
            {
                "override" => $"这 {files.Length} 个文件有 {group.ResourceKeys.Count} 项资源编号相同，其中 {different} 项内容不同；游戏对每个编号只会使用一份资源",
                "collection" => $"合集里包含{files.Length - 1}个另外单独放着的文件，"
                    + $"{group.Members.Count(member => member.ContainedContentIdentical == true)}个已被包含，"
                    + $"{group.Members.Count(member => member.ContainedContentIdentical == false)}个存在不同。还有{group.BundleUniqueResourceCount}样资源只在合集里有。",
                _ => (files.Length == 2 ? "两份文件" : $"这 {files.Length} 份文件") + $"有 {group.ResourceKeys.Count} 项资源编号相同"
                    + (different > 0 ? $"，其中 {different} 项内容不同，可能是同一 mod 的不同版本" : ""),
            };
            cards.Add(new(key, type, title, type == "override" ? ConflictPairPriority.NeedsReview : ConflictPairPriority.CanOrganize,
                files, explanation, Package: group));
        }
        var modules = Scripts.SelectMany(pair => pair.Modules).GroupBy(match => match.ModuleName, StringComparer.Ordinal);
        foreach (var matches in modules)
        {
            token.ThrowIfCancellationRequested();
            var collision = matches.First().Collision;
            var files = collision.Occurrences.Where(item => !removedPaths.Contains(item.ArchivePath) && !hashChangedPaths.Contains(item.ArchivePath))
                .DistinctBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(item => new ConflictFile(item.ArchivePath, item.ArchiveStamp)).ToArray();
            if (files.Length < 2) continue;
            var pairs = Scripts.Where(pair => files.Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, pair.FirstPath))
                && files.Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, pair.SecondPath)))
                .SelectMany(pair => pair.Modules).Where(match => match.ModuleName == collision.ModuleName).ToArray();
            if (pairs.Length == 0) continue;
            var priority = pairs.Min(match => match.Priority);
            cards.Add(new("module" + Identity(collision.ModuleName), "scripts", collision.ModuleName, priority, files,
                priority == ConflictPairPriority.NeedsReview ? "这几个 mod 带了同名脚本，游戏只会加载其中一份"
                    : priority == ConflictPairPriority.CanOrganize ? "这些文件带的这个脚本模块内容相同" : "部分脚本未完成比较，暂时无法确认全部内容是否相同",
                Module: collision));
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
                    .Select(file => new ConflictFile(file.Path, new(file.Size, file.LastWriteTimeUtc))).ToArray(), "", Duplicate: duplicate));
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
                    || view == "scripts" && card.Module is not null || view == "duplicates" && card.Duplicate is not null);
            cards.Add((card.Priority, card.Key, included, () =>
            {
                var kind = card.Package is not null ? ConflictRowKind.Resource : card.Module is not null ? ConflictRowKind.Module : ConflictRowKind.Duplicate;
                if (!add(new(card.Key, kind, 0, card.Title, Priority: card.Priority, IsExpandable: true,
                    FileCountText: $"{card.Files.Count} 个文件", IsIgnored: ignored, OwnerKey: card.Key))) return;
                add(new(card.Key + "/explanation", ConflictRowKind.Explanation, 1,
                    card.Duplicate is null ? card.Explanation : $"共有 {DuplicateGroupCount} 组重复文件，",
                    OwnerKey: card.Key, IsIgnored: ignored, DuplicateLink: card.Duplicate is null ? null : Link(card)));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var contentLabels = new HashSet<string>(StringComparer.Ordinal);
                var activeFiles = card.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var moduleContentCounts = card.Module?.ContentGroups.ToDictionary(group => group.Sha256,
                    group => group.Occurrences.Where(item => activeFiles.Contains(item.ArchivePath))
                        .Select(item => item.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.Ordinal);
                var visibleIndex = 0;
                foreach (var file in card.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(file.Path)) continue;
                    var duplicate = card.Duplicate is null ? DuplicateFor(file.Path) : null;
                    var siblings = duplicate?.Files.Where(copy => card.Files.Any(member => StringComparer.OrdinalIgnoreCase.Equals(member.Path, copy.Path))).ToArray() ?? [];
                    var representative = siblings.FirstOrDefault(copy => StringComparer.OrdinalIgnoreCase.Equals(copy.Path, duplicate!.Duplicate!.SuggestedKeepPath))
                        ?? siblings.FirstOrDefault() ?? file;
                    seen.UnionWith(siblings.Select(copy => copy.Path));
                    var key = FileKey(card.Key, representative.Path);
                    var fileIgnored = ignoredFiles.Contains(key);
                    if (!showIgnored && fileIgnored) continue;
                    if (card.Module is { ContentGroups.Count: > 1 } module && card.Files.Count >= 3)
                    {
                        var contentGroup = module.ContentGroups.FirstOrDefault(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, representative.Path)));
                        if (contentGroup is not null && moduleContentCounts![contentGroup.Sha256] > 1 && contentLabels.Add(contentGroup.Sha256))
                            add(new(card.Key + "/content" + contentLabels.Count, ConflictRowKind.Content, 1,
                                $"以下 {moduleContentCounts[contentGroup.Sha256]} 个内容相同", OwnerKey: card.Key, IsIgnored: ignored));
                    }
                    var member = card.Package?.Members.First(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, representative.Path));
                    var sharedResources = card.Package?.Relations.Where(relation =>
                            StringComparer.OrdinalIgnoreCase.Equals(relation.Pair.FirstPath, representative.Path)
                            || StringComparer.OrdinalIgnoreCase.Equals(relation.Pair.SecondPath, representative.Path))
                        .SelectMany(relation => relation.Pair.Resources).GroupBy(match => match.Key).ToArray();
                    var differentCount = sharedResources?.Count(matches => matches.Any(match => match.HasConfirmedDifference)) ?? 0;
                    var annotation = member is null ? card.Duplicate is not null && visibleIndex > 0 ? "内容完全相同" : ""
                        : member.IsBundle ? $"独有 {member.UniqueResourceCount} 样"
                        : member.ContainedContentIdentical is { } identical ? identical ? "已被包含" : "存在不同"
                        : $"撞 {differentCount} 样 · 独有 {member.UniqueResourceCount} 样";
                    var annotationTooltip = member is not null && !member.IsBundle && member.ContainedContentIdentical is null
                        ? $"相同TGI编号{sharedResources!.Length}个，其中{sharedResources.Count(matches => matches.All(match => match.HasConfirmedIdentical && !match.HasConfirmedDifference))}个内容相同，独有编号{member.UniqueResourceCount}个"
                        : null;
                    var tag = member?.IsBundle == true ? "合集"
                        : card.Type == "suspected" && representative.Stamp?.LastWriteTimeUtc == card.Files.Max(item => item.Stamp?.LastWriteTimeUtc) ? "最新"
                        : card.Duplicate is not null && StringComparer.OrdinalIgnoreCase.Equals(representative.Path, card.Duplicate.SuggestedKeepPath) ? "建议保留" : "";
                    add(new(key, ConflictRowKind.File, 1, representative.Name, representative.Path + "\n" + representative.Metadata,
                        Tooltip: representative.Path, OwnerKey: card.Key, File: representative, Annotation: annotation, Tag: tag,
                        IsIgnored: ignored || fileIgnored, IsBundle: member?.IsBundle == true,
                        DuplicateLink: duplicate is null ? null : Link(duplicate), CopyCount: duplicate is null ? 0 : duplicate.Files.Count - 1,
                        AnnotationTooltip: annotationTooltip));
                    visibleIndex++;
                }
                if (card.Duplicate is not null) return;
                var technicalKey = card.Key + "/technical";
                if (!add(new(technicalKey, ConflictRowKind.Technical, 1, TechnicalDetailTitle, IsExpandable: true,
                    OwnerKey: card.Key, IsIgnored: ignored))) return;
                var entries = TechnicalEntries(card);
                var references = Cards.Where(other => other.Key != card.Key && other.ResourceKeys.Intersect(card.ResourceKeys).Any()).ToArray();
                var copyText = card.Title + " · 第 " + card.GroupIndex + " 组\n" + string.Join("\n\n", entries.Select(entry => entry.CopyText))
                    + string.Concat(references.Select(other => $"\n相关依据也见第 {other.GroupIndex} 组"));
                var index = 0;
                // 文件很多时按列分段，分段仍是同一虚拟化列表的行，避免一次创建所有版本控件。
                for (var start = 0; start < card.Files.Count; start += 6)
                {
                    var count = Math.Min(6, card.Files.Count - start);
                    var headers = card.Files.Skip(start).Take(count).Select(file => new ConflictTechnicalCell(file.Name, "", file.Path)).ToArray();
                    add(new(technicalKey + "/header" + start, ConflictRowKind.TechnicalHeader, 1, "TGI／模块", OwnerKey: card.Key,
                        IsIgnored: ignored, TechnicalEntry: new("TGI／模块", "种类", headers)));
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var part = entry with { Cells = entry.Cells.Skip(start).Take(count).Select(cell => cell with { FileName = "" }).ToArray() };
                        add(new(technicalKey + "/value" + index++, ConflictRowKind.Technical, 1, part.Identifier,
                            Tooltip: part.CopyText, OwnerKey: card.Key, TechnicalEntry: part, IsIgnored: ignored));
                    }
                }
                foreach (var reference in references)
                    add(new(technicalKey + "/reference/" + reference.Key, ConflictRowKind.GroupReference, 1,
                        $"相关依据也见第 {reference.GroupIndex} 组 →", OwnerKey: card.Key, IsIgnored: ignored, RelatedLink: Link(reference)));
                add(new(technicalKey + "/copy", ConflictRowKind.CopyDetails, 1, "复制详情", OwnerKey: card.Key,
                    CopyText: copyText, IsIgnored: ignored));
            }));
        }
    }

    private static bool MatchesScannedStamp(DuplicateFile file, PackageConflictScanReport? packages, ScriptModuleScanReport? scripts)
    {
        var stamp = new FileStamp(file.Size, file.LastWriteTimeUtc);
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
                var cells = card.Files.Select(file =>
                {
                    var index = comparison.ContentGroups.ToList().FindIndex(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.PackagePath, file.Path)));
                    var group = index < 0 ? null : comparison.ContentGroups[index];
                    var failed = comparison.Uncompared.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.PackagePath, file.Path));
                    var version = group is not null ? $"版本 {index + 1}" : failed is not null ? $"未完成：{failed.Issue!.Message}" : "—";
                    var evidence = file.Path + "\n" + file.TechnicalMetadata;
                    if (group is not null) evidence += $"\nSHA256 {group.Sha256} · 资源解压大小 {group.DecompressedLength} 字节";
                    if (failed is not null) evidence += $"\n{failed.Issue!.Code} · {failed.Issue.Stage} · {failed.Issue.Detail ?? failed.Issue.Message}";
                    return new ConflictTechnicalCell(file.Name, version, evidence);
                }).ToArray();
                return new ConflictTechnicalEntry(key.Tgi, ResourceTypeName(key.Type), cells);
            }).ToArray();
        }
        if (card.Module is { } module)
            return [new(module.ModuleName, "脚本模块", card.Files.Select(file =>
            {
                var index = module.ContentGroups.ToList().FindIndex(group => group.Occurrences.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path)));
                var occurrence = module.Occurrences.First(item => StringComparer.OrdinalIgnoreCase.Equals(item.ArchivePath, file.Path));
                var version = index >= 0 ? $"版本 {index + 1}" : occurrence.Issue is { } failure ? $"未完成：{failure.Message}" : "未完成";
                var evidence = file.Path + "\n" + file.TechnicalMetadata + "\n" + occurrence.EntryName;
                if (occurrence.IsSuccess) evidence += $"\nSHA256 {occurrence.Sha256} · 模块解压大小 {occurrence.ContentLength} 字节";
                if (occurrence.Issue is { } issue) evidence += $"\n{issue.Code} · {issue.Stage} · {issue.Detail ?? issue.Message}";
                return new ConflictTechnicalCell(file.Name, version, evidence);
            }).ToArray())];
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
        if (main is null && Problems.Any(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, path)))
            main = new(path, PackageReport?.ResourceInventories.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, path))?.ScannedStamp);
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
        foreach (var card in own.Where(card => card.Module is not null))
            foreach (var pair in Scripts.Where(pair => !removedPaths.Contains(pair.FirstPath) && !removedPaths.Contains(pair.SecondPath)
                && (StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, path) || StringComparer.OrdinalIgnoreCase.Equals(pair.SecondPath, path))
                && pair.Modules.Any(match => match.ModuleName == card.Module!.ModuleName)))
                Add(StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, path) ? pair.SecondPath : pair.FirstPath,
                    new("同名脚本", card.Module!.ModuleName, Link(card)));
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
