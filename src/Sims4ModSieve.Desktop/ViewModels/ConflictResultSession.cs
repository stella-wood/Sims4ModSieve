using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Desktop.ViewModels;

public enum ConflictRowKind { PackagePair, ScriptPair, Resource, Module, Content, File, Technical, Explanation, Problem, RangeProblem, TechnicalHeader, CopyDetails, Duplicate, GroupReference, TechnicalListHeader, TechnicalListFile, TechnicalScroll }

public sealed record ConflictResultRow(
    string Key, ConflictRowKind Kind, int Depth, string Title,
    string Detail = "", string Tooltip = "", string SecondTitle = "", string SecondTooltip = "",
    ConflictPairPriority? Priority = null, bool IsExpandable = false, bool IsExpanded = false, bool IsCardEnd = false,
    int GroupIndex = 0, string OwnerKey = "", ConflictFile? File = null,
    string Annotation = "", string Tag = "", string FileCountText = "",
    bool IsIgnored = false, bool IsBundle = false,
    ConflictGroupLink? DuplicateLink = null, int CopyCount = 0,
    ConflictTechnicalEntry? TechnicalEntry = null, string CopyText = "", ConflictGroupLink? RelatedLink = null,
    string? AnnotationTooltip = null, ConflictTechnicalTableState? TechnicalTable = null)
{
    public string StateText => Priority switch
    {
        ConflictPairPriority.NeedsReview => "需查看",
        ConflictPairPriority.CanOrganize => "可整理",
        ConflictPairPriority.Incomplete => "未完成",
        _ => string.Empty,
    };
    public bool HasState => Priority is not null;
    public bool HasSecondTitle => SecondTitle.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public bool IsCardStart => Depth == 0;
    public bool IsProblemFileContent => OwnerKey.StartsWith("problem/", StringComparison.Ordinal)
        && Kind is ConflictRowKind.Problem or ConflictRowKind.Explanation or ConflictRowKind.File;
    public bool IsTechnicalToggle => Kind == ConflictRowKind.Technical && IsExpandable;
    public bool HasAnnotation => Annotation.Length > 0;
    public bool HasTag => Tag.Length > 0;
    public bool HasCopies => CopyCount > 0;
    public string CopiesLabel => $"另有{CopyCount}份重复 →";
    public bool IsDuplicateExplanation => Kind == ConflictRowKind.Explanation && DuplicateLink is not null;
    public string GroupLabel => GroupIndex > 0 ? $"第 {GroupIndex} 组" : string.Empty;
    public string ExpandName => (IsExpanded ? "收起 " : "展开 ") + Title;
}

// 顺序也用于主要原因影响条目数相同时的稳定选择。
public enum ConflictProblemCategory { Corrupt, Unsupported, EntryTooLarge, Changed, Access, FileTooLarge, Budget, Unknown, Placeholder }

// ResourceType 只由收集失败资源时传入，不从错误文字推断。
public sealed record ConflictProblemReason(string Code, string? EntryName, string? Detail, ConflictProblemCategory Category,
    uint? ResourceType = null)
{
    public string Summary => ConflictResultSession.GetProblemSummary(Category);
}

public sealed record ConflictProblemRow(
    string Path, bool IsWholeFile, int? IncompleteCount, string Summary, IReadOnlyList<ConflictProblemReason> Reasons,
    IReadOnlyList<string> EntryNames, bool WholeFileNeedsAttention)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string CountText => IncompleteCount is { } count ? $"未完成 {count} 项" : "数量未知";
    public string EntryNamesText => EntryNames.Count <= 3
        ? string.Join("、", EntryNames)
        : string.Join("、", EntryNames.Take(3)) + $" 等 {EntryNames.Count} 项";
    // 整个文件未分析也由已读取的条目触发时，数量未知仍要给出条目名。
    public string DetailText => IsWholeFile && WholeFileNeedsAttention
        ? "整个文件未分析 · " + EntryNamesText : CountText + (EntryNamesText.Length > 0 ? " · " + EntryNamesText : "");
}

public sealed record ConflictRangeProblem(string Path, string Summary, string Code, string? Detail, bool IsScript = false);

public sealed partial class ConflictResultSession
{
    private const string TechnicalDetailTitle = "技术详情";
    private readonly HashSet<string> expandedKeys = new(StringComparer.Ordinal);
    public IReadOnlyList<PackageConflictPairSummary> Resources { get; private set; } = [];
    public IReadOnlyList<ScriptConflictPairSummary> Scripts { get; private set; } = [];
    public IReadOnlyList<ConflictProblemRow> Problems { get; private set; } = [];
    public IReadOnlyList<ConflictRangeProblem> RangeProblems { get; private set; } = [];
    public PackageConflictScanReport? PackageReport { get; private set; }
    public ResourceContentComparisonReport? ContentReport { get; private set; }
    public ScriptModuleScanReport? ScriptReport { get; private set; }

    public void Reset()
    {
        Resources = [];
        Scripts = [];
        Problems = [];
        RangeProblems = [];
        PackageReport = null;
        ContentReport = null;
        ScriptReport = null;
        expandedKeys.Clear();
        ResetGroups();
    }

    public void ToggleExpanded(string key)
    {
        if (!expandedKeys.Remove(key)) expandedKeys.Add(key);
    }

    public void ToggleAllGroups(IEnumerable<ConflictResultRow> visibleRows)
    {
        var keys = visibleRows.Where(row => row.IsCardStart && row.IsExpandable).Select(row => row.Key).ToArray();
        var expand = !keys.All(expandedKeys.Contains);
        foreach (var key in keys)
            if (expand) expandedKeys.Add(key); else expandedKeys.Remove(key);
    }

    public IReadOnlySet<string> GetExpandedKeysSnapshot() => new HashSet<string>(expandedKeys, StringComparer.Ordinal);

    public void Replace(PackageConflictScanReport packages, ResourceContentComparisonReport contents,
        ScriptModuleScanReport scripts, CancellationToken cancellationToken = default,
        DuplicateReport? duplicates = null)
    {
        var groupingBuilder = new PackageConflictGroupingBuilder();
        var prepared = groupingBuilder.Prepare(packages, contents, cancellationToken: cancellationToken);
        var stampMismatches = duplicates?.Groups.SelectMany(group => group.Files)
            .Where(file => !MatchesScannedStamp(file, packages, scripts))
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var changedPaths = (duplicates?.Issues ?? []).Where(issue => issue.Code == "file-changed-during-hash")
            .Select(issue => issue.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changedPaths.UnionWith(stampMismatches.Select(file => file.Path));
        // 缓存输入也只保留稳定文件，删除和撤回重建不能重新使用旧版本的关系。
        prepared = prepared with
        {
            Files = prepared.Files.Where(file => !changedPaths.Contains(file.Path)).ToArray(),
            Pairs = prepared.Pairs.Where(pair => !changedPaths.Contains(pair.FirstPath) && !changedPaths.Contains(pair.SecondPath)).ToArray(),
        };
        var grouping = groupingBuilder.Build(prepared, new PackageConflictGroupingOptions(), cancellationToken);
        var builder = new ConflictPairSummaryBuilder();
        var resources = builder.BuildPackagePairs(packages, contents, cancellationToken)
            .Where(pair => !changedPaths.Contains(pair.FirstPath) && !changedPaths.Contains(pair.SecondPath)).ToArray();
        var modules = builder.BuildScriptPairs(scripts, cancellationToken)
            .Where(pair => !changedPaths.Contains(pair.FirstPath) && !changedPaths.Contains(pair.SecondPath)).ToArray();
        var problems = new Dictionary<string, (bool Whole, HashSet<string> Entries, HashSet<ConflictProblemReason> Reasons)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool whole, string? identity, ConflictProblemReason reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!problems.TryGetValue(path, out var entry)) entry = (false, new(StringComparer.Ordinal), []);
            entry.Whole |= whole;
            if (identity is not null) entry.Entries.Add(identity);
            entry.Reasons.Add(reason);
            problems[path] = entry;
        }

        foreach (var path in packages.IncompletePackagePaths)
            Add(path, true, null, new("package-index-incomplete", null, null, ConflictProblemCategory.Placeholder));
        foreach (var comparison in contents.Comparisons)
            foreach (var occurrence in comparison.Uncompared)
            {
                var issue = occurrence.Issue;
                Add(occurrence.PackagePath, false, "resource:" + comparison.Key.Tgi,
                    new(issue?.Code ?? "resource-read-unknown", comparison.Key.Tgi, issue?.Detail,
                        Classify(issue?.Code, resourceStage: issue?.Stage), comparison.Key.Type));
            }
        foreach (var path in scripts.IncompleteArchivePaths)
            Add(path, true, null, new("script-archive-incomplete", null, null, ConflictProblemCategory.Placeholder));
        foreach (var collision in scripts.Collisions)
            foreach (var occurrence in collision.Uncompared)
            {
                var issue = occurrence.Issue;
                Add(occurrence.ArchivePath, false, "script:" + occurrence.EntryName,
                    new(issue?.Code ?? "script-read-unknown", occurrence.EntryName, issue?.Detail,
                        Classify(issue?.Code, scriptStage: issue?.Stage)));
            }

        var ranges = new List<ConflictRangeProblem>();
        foreach (var excluded in grouping.ExcludedFiles)
        {
            foreach (var issue in excluded.InputIssues)
                Add(excluded.Path, excluded.FailedResources.Count == 0, null, new(issue.Code, issue.Key?.Tgi, issue.Message, ConflictProblemCategory.Unknown, issue.Key?.Type));
            foreach (var version in excluded.InvalidVersions)
                Add(excluded.Path, true, null, new("grouping-version-unstable", null, null, ConflictProblemCategory.Changed));
        }
        foreach (var issue in packages.Issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = Classify(issue.Code, packageStage: issue.ReadStage);
            if (issue.Stage is PackageConflictScanIssueStage.SourceValidation or PackageConflictScanIssueStage.Discovery
                || issue.Code is "entry-metadata-failed" or "source-metadata-failed")
                ranges.Add(new(issue.Path, "检查范围无法访问", issue.Code, issue.Detail));
            else Add(issue.Path, true, null, new(issue.Code, null, issue.Detail, category));
        }
        foreach (var issue in scripts.Issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = Classify(issue.Code, scriptStage: issue.Stage);
            if (issue.Stage is ScriptArchiveIssueStage.SourceValidation or ScriptArchiveIssueStage.Discovery
                || issue.Code is "entry-metadata-failed" or "source-metadata-failed")
                ranges.Add(new(issue.Path, "检查范围无法访问", issue.Code, issue.Detail, IsScript: true));
            else Add(issue.Path, issue.EntryName is null, issue.EntryName is null ? null : "script:" + issue.EntryName,
                new(issue.Code, issue.EntryName, issue.Detail, category));
        }

        foreach (var issue in duplicates?.Issues ?? [])
        {
            if (issue.Stage is ScanIssueStage.SourceValidation or ScanIssueStage.Discovery)
            {
                if (!ranges.Any(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, issue.Path) && problem.Code == issue.Code))
                    ranges.Add(new(issue.Path, "检查范围无法访问", issue.Code, issue.Message));
            }
            else Add(issue.Path, true, null, new(issue.Code, null, issue.Message,
                issue.Code == "file-changed-during-hash" ? ConflictProblemCategory.Changed : ConflictProblemCategory.Access));
        }
        foreach (var file in stampMismatches)
            Add(file.Path, true, null, new("hash-scan-stamp-mismatch", null,
                $"整文件哈希扫描：{file.Size} 字节 · {file.LastWriteTimeUtc:O}；与资源／脚本扫描文件戳不一致",
                ConflictProblemCategory.Changed));

        var mappedProblems = new List<ConflictProblemRow>(problems.Count);
        foreach (var (path, entry) in problems.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reasons = entry.Reasons.OrderBy(reason => reason.Category)
                .ThenBy(reason => reason.EntryName, StringComparer.Ordinal).ThenBy(reason => reason.Code, StringComparer.Ordinal).ToArray();
            var category = reasons.Where(reason => reason.Category != ConflictProblemCategory.Placeholder)
                .GroupBy(reason => reason.Category)
                .OrderByDescending(group => group.Select(reason => reason.EntryName ?? string.Empty).Distinct(StringComparer.Ordinal).Count())
                .ThenBy(group => group.Key).Select(group => group.Key).FirstOrDefault(ConflictProblemCategory.Unknown);
            var entryNames = reasons.Select(reason => reason.EntryName).Where(name => name is not null).Distinct(StringComparer.Ordinal).ToArray();
            mappedProblems.Add(new(path, entry.Whole, entry.Whole ? null : entry.Entries.Count, GetProblemSummary(category), reasons,
                entryNames!, entryNames!.Length > 0));
        }
        cancellationToken.ThrowIfCancellationRequested();
        Reset();
        PackageReport = packages;
        ContentReport = contents;
        ScriptReport = scripts;
        Resources = resources;
        Scripts = modules;
        Problems = mappedProblems.AsReadOnly();
        RangeProblems = ranges.Distinct().ToArray();
        InitializeGroups(prepared, grouping, duplicates, changedPaths, cancellationToken);
    }

    public IReadOnlyList<ConflictResultRow> BuildVisibleRows(string view, string filter,
        IReadOnlySet<string> expanded, CancellationToken cancellationToken = default,
        bool showIgnored = false, IReadOnlySet<string>? types = null)
    {
        var rows = new List<ConflictResultRow>();
        var cards = new List<(ConflictPairPriority Priority, string Key, bool Included, Action Build)>();
        var currentOwnerKey = string.Empty;
        bool Add(ConflictResultRow row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = row.OwnerKey.Length > 0 ? row.OwnerKey : currentOwnerKey;
            var fileIgnored = row.File is not null && ignoredFiles.Contains(FileKey(owner, row.File.Path));
            if (fileIgnored && !showIgnored) return false;
            var open = row.IsExpandable && expanded.Contains(row.Key);
            rows.Add(row with { IsExpanded = open, Title = row.IsTechnicalToggle && open ? "收起" : row.Title,
                OwnerKey = owner, IsIgnored = row.IsIgnored || fileIgnored || ignoredGroups.Contains(owner) });
            return open;
        }
        void Technical(string key, string text) =>
            Add(new(key, ConflictRowKind.Technical, 1, text, Tooltip: text));
        void Detail(string key, Func<IEnumerable<string>> values)
        {
            if (!Add(new(key + "/technical", ConflictRowKind.Technical, 1, TechnicalDetailTitle, IsExpandable: true))) return;
            var index = 0;
            foreach (var value in values()) Technical(key + "/technical" + index++, value);
        }
        void Explanation(string owner, string text) => Add(new(owner + "/explanation", ConflictRowKind.Explanation, 1, text));
        void Card(ConflictPairPriority priority, string key, bool included, Action build) => cards.Add((priority, key, included, build));

        AddGroupCards(cards, Add, view, expanded, showIgnored, types, cancellationToken);
        var moduleGroups = Scripts.SelectMany(pair => pair.Modules).GroupBy(match => match.ModuleName, StringComparer.Ordinal).ToArray();
        // 已出现在资源／模块卡里的失败条目，不再复制为未完成文件卡；未碰撞的失败仍保留。
        var represented = new Dictionary<string, HashSet<(string Entry, string Code)>>(StringComparer.OrdinalIgnoreCase);
        void Represent(string path, string entry, string code)
        {
            if (!represented.TryGetValue(path, out var entries)) represented[path] = entries = [];
            entries.Add((entry, code));
        }
        foreach (var matches in moduleGroups)
            foreach (var occurrence in matches.First().Collision.Uncompared) Represent(occurrence.ArchivePath, occurrence.EntryName, occurrence.Issue?.Code ?? "script-read-unknown");
        foreach (var problem in Problems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (removedPaths.Contains(problem.Path)) continue;
            var isScript = Path.GetExtension(problem.Path).Equals(".ts4script", StringComparison.OrdinalIgnoreCase);
            var reasons = problem.Reasons.Where(reason => reason.EntryName is null
                || !represented.TryGetValue(problem.Path, out var entries) || !entries.Contains((reason.EntryName, reason.Code))).ToArray();
            if (reasons.Length == 0) continue;
            var key = "problem" + Identity(problem.Path.ToUpperInvariant());
            Card(ConflictPairPriority.Incomplete, key, view is "all" or "problems" || view == (isScript ? "scripts" : "resources"), () =>
            {
                var file = new ConflictFile(problem.Path, PackageReport?.ResourceInventories
                    .FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, problem.Path))?.ScannedStamp);
                var reasonTexts = reasons.Select(reason => ProblemEntryText(reason.Category,
                    reason.ResourceType is { } type ? GetResourceTypeDisplayName(type) : reason.EntryName)).Distinct(StringComparer.Ordinal).ToArray();
                Add(new(key, ConflictRowKind.Problem, 0, problem.Name, Tooltip: problem.Path,
                    Priority: ConflictPairPriority.Incomplete, Annotation: reasonTexts[0]));
                foreach (var (text, index) in reasonTexts.Select((text, index) => (text, index)).Skip(1))
                {
                    Add(new(key + "/reason" + index, ConflictRowKind.Explanation, 1, text));
                }
                Add(new(key + "/file" + Identity(problem.Path.ToUpperInvariant()), ConflictRowKind.File, 1, problem.Name,
                    problem.Path + "\n" + file.Metadata, Tooltip: problem.Path, File: file));
                Detail(key, () => new[] { problem.Path }.Concat(reasons.Select(reason =>
                    ProblemReasonText(reason.Code, reason.Detail, reason.Category, reason.ResourceType, reason.EntryName))));
            });
        }
        for (var index = 0; index < RangeProblems.Count; index++)
        {
            var problem = RangeProblems[index];
            if (removedPaths.Contains(problem.Path)) continue;
            var key = "range" + Identity(problem.Path.ToUpperInvariant()) + index;
            Card(ConflictPairPriority.Incomplete, key, view is "all" or "problems" || view == (problem.IsScript ? "scripts" : "resources"), () =>
            {
                if (!Add(new(key, ConflictRowKind.RangeProblem, 0, "检查范围问题", Tooltip: problem.Path,
                    Priority: ConflictPairPriority.Incomplete, IsExpandable: true))) return;
                Explanation(key, problem.Summary);
                Detail(key, () => [problem.Path, problem.Code + " · " + problem.Detail]);
            });
        }
        var groupIndex = 0;
        foreach (var card in cards.OrderBy(card => card.Priority).ThenBy(card => groupNumbers.GetValueOrDefault(card.Key, int.MaxValue)).ThenBy(card => card.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupIndex++;
            if (!card.Included || !Matches(filter, card.Priority) || !showIgnored && ignoredGroups.Contains(card.Key)) continue;
            var firstRow = rows.Count;
            currentOwnerKey = card.Key;
            card.Build();
            rows[firstRow] = rows[firstRow] with { GroupIndex = groupNumbers.GetValueOrDefault(card.Key, groupIndex) };
            rows[^1] = rows[^1] with { IsCardEnd = true };
        }
        return rows.AsReadOnly();
    }

    private static bool Matches(string filter, ConflictPairPriority priority) => filter == "all"
        || filter.Split(',').Contains(priority switch
        {
            ConflictPairPriority.NeedsReview => "different",
            ConflictPairPriority.CanOrganize => "same",
            _ => "incomplete",
        }, StringComparer.Ordinal);
    private static string Identity(string text) => $"/{text.Length}:{text}";
    private static string ProblemTechnical(string? code, string? detail, ConflictProblemCategory category) =>
        GetProblemSummary(category) + " · " + code + (detail is null ? "" : " · " + detail);

    private static string ProblemEntryText(ConflictProblemCategory category, string? subject) =>
        (string.IsNullOrEmpty(subject) ? "" : subject + " · ") + GetProblemSummary(category);

    private static string ProblemReasonText(string? code, string? detail, ConflictProblemCategory category,
        uint? resourceType, string? entryName) =>
        (entryName is null ? "" : (resourceType is { } type ? GetResourceTypeDisplayName(type) + " · " + entryName : entryName) + " · ")
        + ProblemTechnical(code, detail, category);

    public static string GetProblemSummary(ConflictProblemCategory category) => category switch
    {
        ConflictProblemCategory.Corrupt => "文件损坏，建议重新下载",
        ConflictProblemCategory.Unsupported => "格式暂不支持",
        ConflictProblemCategory.EntryTooLarge => "单条资源过大",
        ConflictProblemCategory.Changed => "扫描期间文件被修改",
        ConflictProblemCategory.Access => "无法访问",
        ConflictProblemCategory.FileTooLarge => "文件过大",
        ConflictProblemCategory.Budget => "分析额度用尽",
        ConflictProblemCategory.Placeholder => "整个文件未分析",
        _ => "未知读取失败",
    };

    private static ConflictProblemCategory Classify(string? code, ResourceContentStage? resourceStage = null,
        ScriptArchiveIssueStage? scriptStage = null, PackageReadStage? packageStage = null)
    {
        if (code is PackageReadIssueCode.MagicMismatch or PackageReadIssueCode.TooSmallForHeader
            or PackageReadIssueCode.IndexOutOfBounds or PackageReadIssueCode.IndexSizeMismatch or PackageReadIssueCode.DuplicateResourceKeys
            or ResourceContentIssueCode.Corrupt or ResourceContentIssueCode.Truncated or ResourceContentIssueCode.OutOfBounds
            or ResourceContentIssueCode.LengthMismatch or ScriptArchiveIssueCode.NotZip or ScriptArchiveIssueCode.Corrupt
            or ScriptArchiveIssueCode.EntryCorrupt) return ConflictProblemCategory.Corrupt;
        if (code is PackageReadIssueCode.UnsupportedVersion or PackageReadIssueCode.UnsupportedIndexVersion
            or ResourceContentIssueCode.UnsupportedCompression or ResourceContentIssueCode.Deleted
            or ScriptArchiveIssueCode.Zip64Unsupported or ScriptArchiveIssueCode.PycUnsupported
            or ScriptArchiveIssueCode.PycStale or ScriptArchiveIssueCode.PycTimestampUnknown) return ConflictProblemCategory.Unsupported;
        if (code is ResourceContentIssueCode.StoredSizeExceedsLimit or ResourceContentIssueCode.DecompressedSizeExceedsLimit
            or ScriptArchiveIssueCode.EntrySizeExceedsLimit) return ConflictProblemCategory.EntryTooLarge;
        if (resourceStage == ResourceContentStage.Stability || scriptStage == ScriptArchiveIssueStage.Stability
            || packageStage == PackageReadStage.Stability || code is ResourceContentIssueCode.ChangedSinceScan
                or ResourceContentIssueCode.ChangedDuringRead or PackageReadIssueCode.ChangedDuringRead or ScriptArchiveIssueCode.ChangedDuringRead)
            return ConflictProblemCategory.Changed;
        if (resourceStage == ResourceContentStage.Access || scriptStage == ScriptArchiveIssueStage.Access
            || packageStage == PackageReadStage.Access || code is PackageReadIssueCode.AccessFailed or PackageReadIssueCode.NotFound
                or PackageReadIssueCode.PathInvalid or ResourceContentIssueCode.AccessFailed or ScriptArchiveIssueCode.AccessFailed)
            return ConflictProblemCategory.Access;
        if (code is PackageReadIssueCode.TooLarge or PackageReadIssueCode.ResourceCountExceedsLimit
            or ScriptArchiveIssueCode.TooLarge or ScriptArchiveIssueCode.EntryCountExceedsLimit) return ConflictProblemCategory.FileTooLarge;
        if (resourceStage == ResourceContentStage.Budget || scriptStage == ScriptArchiveIssueStage.Budget
            || code is ResourceContentIssueCode.BudgetExhausted or ScriptArchiveIssueCode.BudgetExhausted) return ConflictProblemCategory.Budget;
        return ConflictProblemCategory.Unknown;
    }

    public static string GetResourceTypeDisplayName(uint type) => type switch
    {
        // 离线核对 LlamaLogic.Packages 3.8.2 ResourceType 常量及随包 XML。
        0x034AEECB => "捏人部件",
        0x545AC67A => "模拟数据",
        0xC0DB5AE7 => "物件定义",
        0x319E4F1D => "物件目录",
        0x00B2D882 or 0x2BC04EDF or 0x3453CF95 or 0xBA856C78 or 0x2F7D0004 or 0x2F7D0006 => "图像",
        0x220557DA => "文本表",
        _ when LlamaLogicPackageIndexReader.IsTuningType(type) => "调参",
        _ => "未识别资源",
    };
}
