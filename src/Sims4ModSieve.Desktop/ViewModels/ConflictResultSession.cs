using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Desktop.ViewModels;

public enum ConflictRowKind { PackagePair, ScriptPair, Resource, Module, Content, File, Technical, Explanation, Problem, RangeProblem }

public sealed record ConflictResultRow(
    string Key, ConflictRowKind Kind, int Depth, string Title,
    string Detail = "", string Tooltip = "", string SecondTitle = "", string SecondTooltip = "",
    ConflictPairPriority? Priority = null, bool IsExpandable = false, bool IsExpanded = false)
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

public sealed record ConflictRangeProblem(string Path, string Summary, string Code, string? Detail);

public sealed class ConflictResultSession
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
    }

    public void ToggleExpanded(string key)
    {
        if (!expandedKeys.Remove(key)) expandedKeys.Add(key);
    }

    public IReadOnlySet<string> GetExpandedKeysSnapshot() => new HashSet<string>(expandedKeys, StringComparer.Ordinal);

    public void Replace(PackageConflictScanReport packages, ResourceContentComparisonReport contents,
        ScriptModuleScanReport scripts, CancellationToken cancellationToken = default)
    {
        var builder = new ConflictPairSummaryBuilder();
        var resources = builder.BuildPackagePairs(packages, contents, cancellationToken);
        var modules = builder.BuildScriptPairs(scripts, cancellationToken);
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
                ranges.Add(new(issue.Path, "检查范围无法访问", issue.Code, issue.Detail));
            else Add(issue.Path, issue.EntryName is null, issue.EntryName is null ? null : "script:" + issue.EntryName,
                new(issue.Code, issue.EntryName, issue.Detail, category));
        }

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
    }

    public IReadOnlyList<ConflictResultRow> BuildVisibleRows(string view, string filter,
        IReadOnlySet<string> expanded, CancellationToken cancellationToken = default)
    {
        var rows = new List<ConflictResultRow>();
        bool Add(ConflictResultRow row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var open = row.IsExpandable && expanded.Contains(row.Key);
            rows.Add(row with { IsExpanded = open });
            return open;
        }
        void Technical(string key, int depth, string text) =>
            Add(new(key, ConflictRowKind.Technical, depth, text, Tooltip: text));
        void Detail(string key, int depth, Func<IEnumerable<string>> values)
        {
            if (!Add(new(key + "/technical", ConflictRowKind.Technical, depth, TechnicalDetailTitle, IsExpandable: true))) return;
            var index = 0;
            foreach (var value in values()) Technical(key + "/technical" + index++, depth + 1, value);
        }
        void File(string owner, string path, Func<string> technical)
        {
            var key = owner + "/file" + Identity(path.ToUpperInvariant());
            if (Add(new(key, ConflictRowKind.File, 4, Path.GetFileName(path), Tooltip: path, IsExpandable: true)))
            {
                Technical(key + "/path", 5, path);
                Technical(key + "/entry", 5, technical());
            }
        }
        void Unfinished(string owner, string path, string typeName, string? entryTitle, string? identity, string? code, string? detail,
            ConflictProblemCategory category, int index)
        {
            // 未完成条目按下标区分；同一资源的多个失败条目共用展开键会串状态。
            var key = owner + "/unfinished" + index;
            if (!Add(new(key, ConflictRowKind.Content, 2, Path.GetFileName(path),
                "1 个文件 · " + ProblemEntryText(category, entryTitle is null ? typeName : entryTitle),
                Tooltip: path, Priority: ConflictPairPriority.Incomplete, IsExpandable: true))) return;
            Detail(key, 3, () => [typeName + " · " + (identity ?? "资源未知"), ProblemTechnical(code, detail, category)]);
        }
        void ResourceContents(string owner, ResourceContentComparison comparison)
        {
            Detail(owner, 2, () => ["TGI " + comparison.Key.Tgi]);
            for (var index = 0; index < comparison.ContentGroups.Count; index++)
            {
                var group = comparison.ContentGroups[index];
                var key = owner + "/content" + Identity(group.Sha256);
                if (!Add(new(key, ConflictRowKind.Content, 2, $"内容分组 {index + 1}", $"{group.Occurrences.Count} 个文件", IsExpandable: true))) continue;
                Detail(key, 3, () => new[] { "SHA256 " + group.Sha256 }.Concat(group.Occurrences.Select(
                    occurrence => $"{occurrence.PackagePath} · 解压大小 {occurrence.DecompressedLength} 字节")));
                foreach (var occurrence in group.Occurrences)
                    File(key, occurrence.PackagePath, () => $"解压大小 {occurrence.DecompressedLength} 字节");
            }
            for (var index = 0; index < comparison.Uncompared.Count; index++)
            {
                var occurrence = comparison.Uncompared[index];
                var issue = occurrence.Issue;
                Unfinished(owner, occurrence.PackagePath, GetResourceTypeDisplayName(comparison.Key.Type), null, comparison.Key.Tgi,
                    issue?.Code, issue?.Detail, Classify(issue?.Code, resourceStage: issue?.Stage), index);
            }
        }
        void ModuleContents(string owner, ScriptModuleCollision collision)
        {
            for (var index = 0; index < collision.ContentGroups.Count; index++)
            {
                var group = collision.ContentGroups[index];
                var key = owner + "/content" + Identity(group.Sha256);
                if (!Add(new(key, ConflictRowKind.Content, 2, $"内容分组 {index + 1}", $"{group.Occurrences.Count} 个文件", IsExpandable: true))) continue;
                Detail(key, 3, () => new[] { "SHA256 " + group.Sha256 }.Concat(group.Occurrences.Select(
                    occurrence => occurrence.ArchivePath + " · " + occurrence.EntryName)));
                foreach (var occurrence in group.Occurrences) File(key, occurrence.ArchivePath, () => occurrence.EntryName);
            }
            for (var index = 0; index < collision.Uncompared.Count; index++)
            {
                var occurrence = collision.Uncompared[index];
                Unfinished(owner, occurrence.ArchivePath, "脚本模块", occurrence.EntryName, occurrence.EntryName,
                    occurrence.Issue?.Code, occurrence.Issue?.Detail,
                    Classify(occurrence.Issue?.Code, scriptStage: occurrence.Issue?.Stage), index);
            }
        }

        if (view == "resources")
            foreach (var pair in Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(filter, pair.HasConfirmedDifference, pair.HasConfirmedIdentical)) continue;
                var key = "package" + Identity(pair.FirstPath.ToUpperInvariant()) + Identity(pair.SecondPath.ToUpperInvariant());
                var supplement = pair.FirstContainedInSecond && pair.SecondContainedInFirst ? "两份文件的资源完全一致"
                    : pair.FirstContainedInSecond ? $"{Path.GetFileName(pair.FirstPath)} 已被 {Path.GetFileName(pair.SecondPath)} 完全包含，删除 {Path.GetFileName(pair.FirstPath)} 不会丢失内容"
                    : pair.SecondContainedInFirst ? $"{Path.GetFileName(pair.SecondPath)} 已被 {Path.GetFileName(pair.FirstPath)} 完全包含，删除 {Path.GetFileName(pair.SecondPath)} 不会丢失内容" : "";
                var detail = PairStatistics(pair.DifferentResourceCount, pair.IdenticalResourceCount, pair.IncompleteResourceCount, "资源", pair.IsSuspectedModVersionPair);
                if (supplement.Length > 0) detail += "\n" + supplement;
                if (!Add(new(key, ConflictRowKind.PackagePair, 0, Path.GetFileName(pair.FirstPath), detail, pair.FirstPath,
                    Path.GetFileName(pair.SecondPath), pair.SecondPath, pair.Priority, true))) continue;
                foreach (var match in pair.Resources)
                {
                    var resourceKey = key + "/resource" + Identity(match.Key.Tgi);
                    if (!Add(new(resourceKey, ConflictRowKind.Resource, 1, GetResourceTypeDisplayName(match.Key.Type),
                        match.HasIncomplete ? "未完成 1 项" : "", Priority: match.Priority, IsExpandable: true))) continue;
                    if (match.HasConfirmedDifference) Add(new(resourceKey + "/explanation", ConflictRowKind.Explanation, 2,
                        "这两个 mod 改了同一样东西，游戏只会用其中一个。"));
                    ResourceContents(resourceKey, match.Comparison);
                }
            }
        else if (view == "scripts")
            foreach (var pair in Scripts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(filter, pair.HasConfirmedDifference, pair.HasConfirmedIdentical)) continue;
                var key = "script" + Identity(pair.FirstPath.ToUpperInvariant()) + Identity(pair.SecondPath.ToUpperInvariant());
                if (!Add(new(key, ConflictRowKind.ScriptPair, 0, Path.GetFileName(pair.FirstPath),
                    PairStatistics(pair.DifferentModuleCount, pair.IdenticalModuleCount, pair.IncompleteModuleCount, "模块", pair.IsSuspectedModVersionPair),
                    pair.FirstPath, Path.GetFileName(pair.SecondPath), pair.SecondPath, pair.Priority, true))) continue;
                foreach (var match in pair.Modules)
                {
                    var moduleKey = key + "/module" + Identity(match.ModuleName);
                    if (!Add(new(moduleKey, ConflictRowKind.Module, 1, match.ModuleName, match.HasIncomplete ? "未完成 1 项" : "",
                        Priority: match.Priority, IsExpandable: true))) continue;
                    if (match.HasConfirmedDifference) Add(new(moduleKey + "/explanation", ConflictRowKind.Explanation, 2,
                        "这两个 mod 带了同名脚本，游戏只会加载其中一份。"));
                    ModuleContents(moduleKey, match.Collision);
                }
            }
        else if (view == "problems")
        {
            foreach (var problem in Problems)
            {
                var key = "problem" + Identity(problem.Path.ToUpperInvariant());
                if (!Add(new(key, ConflictRowKind.Problem, 0, problem.Name, problem.CountText + " · " + problem.Summary,
                    problem.Path, Priority: ConflictPairPriority.Incomplete, IsExpandable: true))) continue;
                for (var index = 0; index < problem.Reasons.Count; index++)
                {
                    var reason = problem.Reasons[index];
                    var subject = reason.ResourceType is { } type ? GetResourceTypeDisplayName(type) : reason.EntryName;
                    Add(new(key + "/reason" + index, ConflictRowKind.Explanation, 1, ProblemEntryText(reason.Category, subject)));
                }
                Detail(key, 1, () => new[] { problem.Path }.Concat(problem.Reasons.Select(reason =>
                    ProblemReasonText(reason.Code, reason.Detail, reason.Category, reason.ResourceType, reason.EntryName))));
            }
            for (var index = 0; index < RangeProblems.Count; index++)
            {
                var problem = RangeProblems[index];
                var key = "range" + Identity(problem.Path.ToUpperInvariant()) + index;
                if (!Add(new(key, ConflictRowKind.RangeProblem, 0, "检查范围问题", problem.Summary, problem.Path,
                    Priority: ConflictPairPriority.Incomplete, IsExpandable: true))) continue;
                Detail(key, 1, () => [problem.Path, problem.Code + " · " + problem.Detail]);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows.AsReadOnly();
    }

    private static bool Matches(string filter, bool different, bool identical) => filter switch
    {
        "different" => different,
        "same" => identical,
        _ => true,
    };
    private static string Identity(string text) => $"/{text.Length}:{text}";
    private static string PairStatistics(int different, int identical, int incomplete, string unit, bool suspected) =>
        $"{unit} · 需查看 {different} · 可整理 {identical}" + (incomplete > 0 ? $" · 未完成 {incomplete}" : "")
        + (suspected ? "\n疑似同一 mod 的两个版本" : "");
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
        0x03B33DDF => "调校数据",
        0x545AC67A => "模拟数据",
        0xC0DB5AE7 => "物件定义",
        0x319E4F1D => "物件目录",
        0x00B2D882 or 0x2BC04EDF or 0x3453CF95 or 0xBA856C78 or 0x2F7D0004 or 0x2F7D0006 => "图像",
        0x220557DA => "文本表",
        _ => "其他资源",
    };
}
