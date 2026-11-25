using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Reporting;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Cli;

internal sealed class CliScanReport(string command, string sourceOrigin, string? notice,
    IReadOnlySet<string> ignoredPaths, CliCommands.ScanOptions options,
    long? memoryBudgetBytes = null,
    Action<ConflictGroupingSize>? sizeObserved = null, Action<string>? memoryObserved = null,
    ConflictScanOptions? scanOptions = null, IStableFileHasher? fileHasher = null,
    IResourceContentHasher? resourceHasher = null, ScanCoverage? coverage = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<string, ReportFile> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReportGroup> groups = [];
    private readonly Dictionary<string, int> groupSeverityCounts = new()
    {
        ["abnormal"] = 0, ["risk"] = 0, ["no_effect"] = 0, ["incomplete"] = 0,
    };
    private readonly List<ReportIssue> issues = [];
    private readonly HashSet<(string Path, string Code, string? Entry)> issueKeys = [];
    private readonly IStableFileHasher hasher = fileHasher ?? new StableFileHasher(new PhysicalFileSystemAccess());
    private IReadOnlyList<ScanSourceSnapshot> sources = [];
    private DuplicateReport? duplicates;
    public ConflictGroupingSize? GroupingSize { get; private set; }
    private DateTime startedAtUtc;
    private DateTime completedAtUtc;
    private int groupCount;
    private int abnormalGroupCount;
    private int discoveredFileCount;
    private int? hashedFileCount;
    private int IgnoredCount => files.Values.Count(file => file.Ignored);
    private int IncompleteCount => issues.Count(issue => issue.Severity == "incomplete");
    private int AbnormalCount => abnormalGroupCount + issues.Count(issue => issue.Severity == "abnormal");
    private bool Visible(string path) => !options.HideIgnored || !ignoredPaths.Contains(path);
    public int ExitCode => groupCount == 0 && issues.Count == 0 ? 0 : 1;
    public IReadOnlyList<ScanSourceSnapshot> Sources => sources;

    private bool Complete => GroupingSize?.ExceedsLimit != true && !options.HasOutputFilters && !options.HideIgnored;
    private bool Matches(string kind, string severity) =>
        (options.Kinds.Count == 0 || options.Kinds.Contains(kind))
        && (options.Severities.Count == 0 || options.Severities.Contains(severity));
    private readonly PathSearch pathSearch = new(options.PathContains ?? "");
    private int PathRank(IEnumerable<string> paths) => pathSearch.Rank(paths.Where(Visible));
    private IEnumerable<ReportGroup> OutputGroups => options.SummaryOnly ? []
        : groups.Where(group => group.PathRank >= 0 && Matches(group.Kind, group.Severity))
            .OrderByDescending(group => group.PathRank).Take(options.Limit ?? int.MaxValue);
    private IEnumerable<ReportIssue> OutputIssues => options.SummaryOnly ? []
        : issues.Where(issue => (!issue.IsFile || Visible(issue.Path)) && Matches(issue.Kind, issue.Severity));
    private int OmittedGroupCount => groupCount - OutputGroups.Count();
    private int OmittedIssueCount => issues.Count - OutputIssues.Count();
    private int SeverityCount(string severity) => groupSeverityCounts[severity] + issues.Count(issue => issue.Severity == severity);
    private Dictionary<string, int> SeverityCounts => groupSeverityCounts.Keys.ToDictionary(severity => severity, SeverityCount);
    public string OutputOmissionsText => (Complete ? "" : $"还有 {OmittedGroupCount} 组未列出 · 还有 {OmittedIssueCount} 项读取问题未列出")
        + (options.OmitResourceKeys ? " · 未展开 resourceKeys" : "");

    public string SummaryText =>
        $"发现 {discoveredFileCount} 个文件"
        + (hashedFileCount is { } hashed ? $" · 已核对 {hashed} 个整文件哈希" : "")
        + $" · {groupCount} 组结果 · {AbnormalCount} 项异常 · {IncompleteCount} 项未分析 · 已忽略 {IgnoredCount} 项"
        + $" · 分级（含读取问题）：异常 {SeverityCount("abnormal")}、有隐患 {SeverityCount("risk")}、不影响 {SeverityCount("no_effect")}、未分析 {SeverityCount("incomplete")}"
        + $" · 读取问题 {issues.Count} 项"
        + (Complete ? "" : $" · {OutputOmissionsText}");

    public async Task RunAsync(IReadOnlyList<ScanSource> scanSources, string? modsRoot, IReadOnlyCollection<string> extensions,
        Action<string>? observeFile = null)
    {
        startedAtUtc = DateTime.UtcNow;
        var duplicateScanner = DuplicateScanner.CreateDefault(hasher);
        var snapshot = scanOptions ?? new ConflictScanOptions(StorageProfile: new WindowsStorageProfile());
        DuplicateScanRequest DuplicateRequest(IReadOnlyCollection<string> included) => new(scanSources, modsRoot, included)
        {
            HashConcurrency = snapshot.Concurrency,
            StorageProfile = snapshot.StorageProfile,
            ConcurrencyDecided = snapshot.ConcurrencyDecided,
            Coverage = coverage,
            ObserveFile = observeFile,
        };
        if (command == "scan-duplicates")
        {
            duplicates = await duplicateScanner.ScanAsync(DuplicateRequest(extensions));
            sources = duplicates.Sources;
            discoveredFileCount = duplicates.DiscoveredFileCount;
            hashedFileCount = duplicates.HashedFileCount;
            foreach (var issue in duplicates.Issues) AddIssue(issue);
            foreach (var group in duplicates.Groups) AddDuplicate(group);
        }
        else
        {
            var fileSystem = new PhysicalFileSystemAccess();
            PackageConflictScanReport? packages = null;
            ResourceContentComparisonReport? contents = null;
            if (command == "scan-conflicts")
            {
                packages = await new PackageConflictScanner(fileSystem, LlamaLogicPackageIndexReader.CreateDefault())
                    .ScanAsync(new(scanSources) { Options = snapshot });
                contents = await new ResourceContentComparer(resourceHasher ?? DbpfResourceContentHasher.CreateDefault())
                    .CompareAsync(new(packages.Candidates) { Options = snapshot });
            }

            var scripts = await new ScriptModuleScanner(fileSystem)
                .ScanAsync(new(scanSources)
                {
                    Coverage = command == "scan-scripts" ? coverage : null,
                    ObserveFile = command == "scan-scripts" ? observeFile : null,
                });
            sources = (packages?.Sources ?? []).Concat(scripts.Sources)
                .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            discoveredFileCount = scripts.DiscoveredArchiveCount + (packages?.DiscoveredPackageCount ?? 0);
            foreach (var issue in scripts.Issues)
                AddIssue(issue.Path, issue.Code, issue.Message, issue.Stage.ToString(), issue.EntryName,
                    isFile: issue.Stage is not (ScriptArchiveIssueStage.SourceValidation or ScriptArchiveIssueStage.Discovery)
                        && issue.Code is not ("entry-metadata-failed" or "source-metadata-failed"));

            if (packages is not null && contents is not null)
            {
                var report = await duplicateScanner.ScanAsync(DuplicateRequest([".package", ".ts4script"]));
                hashedFileCount = report.HashedFileCount;
                try
                {
                    await AddConflicts(packages, contents, scripts, report);
                }
                catch (ConflictGroupingTooLargeException exception)
                {
                    issues.Add(new(string.Empty, ConflictGroupingSize.IssueCode, exception.Message,
                        "grouping", null, false, "read_incomplete", "incomplete", exception.Size));
                }
            }
            else
                await AddScripts(scripts, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var rejectedSources = issues.Where(issue => issue.Stage == nameof(ScanIssueStage.SourceValidation))
            .Select(issue => issue.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sources = sources.Where(source => !rejectedSources.Contains(source.Path)).ToArray();

        // A finding's whole-file hash must describe the file version used as its evidence.
        foreach (var issue in issues.Where(issue => issue.IsFile).ToArray())
        {
            if (issue.Code is "file-changed-during-hash" or "hash-scan-stamp-mismatch"
                or PackageReadIssueCode.ChangedDuringRead or ScriptArchiveIssueCode.ChangedDuringRead
                or ResourceContentIssueCode.ChangedDuringRead or ResourceContentIssueCode.ChangedSinceScan
                or "grouping-version-unstable")
                files[issue.Path] = new(issue.Path, null, null, null, ignoredPaths.Contains(issue.Path));
            else if (!files.ContainsKey(issue.Path))
                await GetFileAsync(issue.Path, reportHashFailure: false);
        }
        completedAtUtc = DateTime.UtcNow;
    }

    private async Task AddConflicts(PackageConflictScanReport packages, ResourceContentComparisonReport contents,
        ScriptModuleScanReport scripts, DuplicateReport report)
    {
        foreach (var issue in packages.Issues)
            AddIssue(issue.Path, issue.Code, issue.Message, issue.Stage.ToString(),
                isFile: issue.Stage is not (PackageConflictScanIssueStage.SourceValidation or PackageConflictScanIssueStage.Discovery)
                    && issue.Code is not ("entry-metadata-failed" or "source-metadata-failed"));
        foreach (var issue in report.Issues) AddIssue(issue);
        foreach (var occurrence in contents.Comparisons.SelectMany(comparison => comparison.Uncompared))
            AddIssue(occurrence.PackagePath, occurrence.Issue?.Code ?? "resource-read-unknown",
                occurrence.Issue?.Message ?? "资源内容未比完", "resource", occurrence.Key.Tgi, isFile: true);

        var (input, result, changedPaths, stampMismatches) =
            ConflictResultGrouping.BuildPackages(packages, contents, scripts, report,
                memoryBudgetBytes: memoryBudgetBytes, sizeObserved: size =>
                {
                    GroupingSize = size;
                    sizeObserved?.Invoke(size);
                });
        foreach (var file in stampMismatches)
            AddIssue(file.Path, "hash-scan-stamp-mismatch", "整文件哈希与资源或脚本扫描的文件版本不一致", "stability", isFile: true);
        foreach (var excluded in result.ExcludedFiles)
        {
            foreach (var issue in excluded.InputIssues)
                AddIssue(excluded.Path, issue.Code, issue.Message, "grouping", issue.Key?.Tgi, isFile: true);
            if (excluded.InvalidVersions.Count > 0)
                AddIssue(excluded.Path, "grouping-version-unstable", "比较期间文件版本不稳定", "stability", isFile: true);
        }

        var problemPaths = issues.Where(issue => issue.IsFile).Select(issue => issue.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        problemPaths.UnionWith(packages.IncompletePackagePaths);
        problemPaths.UnionWith(scripts.IncompleteArchivePaths);
        var duplicateGroups = report.Groups.Select(group => group with
        {
            Files = group.Files.Where(file => !changedPaths.Contains(file.Path)
                && !problemPaths.Contains(file.Path)
                && ConflictResultGrouping.MatchesScannedStamp(file, packages, scripts)).ToArray(),
        }).Where(group => group.Files.Count >= 2).ToArray();

        var duplicateHashes = duplicateGroups.SelectMany(group => group.Files.Select(file => (file.Path, group.Sha256)))
            .ToDictionary(copy => copy.Path, copy => copy.Sha256, StringComparer.OrdinalIgnoreCase);
        var (grouping, _) = ConflictResultGrouping.MergeDuplicatePackageGroups(input, result, report, changedPaths, packages, scripts, memoryObserved);
        foreach (var group in grouping.Groups)
        {
            if (group.Kind == PackageConflictRelationKind.SuspectedSameMod && duplicateGroups.Any(duplicate =>
                group.Members.All(member => duplicate.Files.Any(copy => SamePath(copy.Path, member.Path)))))
                continue;

            var (kind, severity, certainty) = group.Kind switch
            {
                PackageConflictRelationKind.BundleContainment => ("bundle_containment", "risk", "confirmed"),
                PackageConflictRelationKind.SuspectedSameMod => ("suspected_same_mod", "risk", "possible"),
                _ => ("resource_conflict", "abnormal", "confirmed"),
            };
            CountGroup(severity);
            var members = new List<object>();
            foreach (var member in group.Members)
            {
                if (!files.ContainsKey(member.Path) && duplicateHashes.TryGetValue(member.Path, out var hash))
                    AddFile(member.Path, hash, member.ScannedStamp);
                var file = await GetFileAsync(member.Path, member.ScannedStamp);
                if (Visible(member.Path))
                    members.Add(new
                    {
                        file.Path, file.Sha256, file.Size, file.LastWriteTimeUtc, file.Ignored,
                        duplicateSha256 = duplicateHashes.GetValueOrDefault(member.Path),
                        member.IsBundle, member.ContainedContentIdentical, member.ResourceCount,
                        member.SharedResourceCount, member.UniqueResourceCount,
                    });
            }
            if (members.Count == 0) continue;
            groups.Add(new(kind, severity, new
            {
                kind, severity, certainty, files = members,
                resourceKeys = group.ResourceKeys.Select(key => key.Tgi),
                resourceKeyCount = group.ResourceKeys.Count,
                resourceTypes = group.ResourceKeys.Select(key => key.Type).Distinct().Order().Select(type => new
                {
                    type = $"0x{type:X8}",
                    typeName = LlamaLogicPackageIndexReader.GetResourceTypeDisplayName(type),
                }),
                bundlePath = group.BundlePath is { } bundle && Visible(bundle) ? bundle : null,
                group.BundleUniqueResourceCount,
                relations = group.Relations.Where(relation => Visible(relation.Pair.FirstPath) && Visible(relation.Pair.SecondPath))
                    .Select(relation => new
                    {
                        relation.Pair.FirstPath, relation.Pair.SecondPath, relation.Overlap,
                        relation.Pair.DifferentResourceCount, relation.Pair.IdenticalResourceCount, relation.Pair.IncompleteResourceCount,
                    }),
            }, PathRank(group.Members.Select(member => member.Path))));
        }

        foreach (var group in duplicateGroups)
        {
            var selection = new DuplicateSelectionService().Select(group.Files.ToArray());
            AddDuplicate(group with { SuggestedKeepPath = selection.KeepPath, SuggestedDeletePaths = selection.DeletePaths });
        }
        await AddScripts(scripts, changedPaths, duplicateHashes);
    }

    private async Task AddScripts(ScriptModuleScanReport scripts, IReadOnlySet<string> changedPaths,
        IReadOnlyDictionary<string, string>? duplicateHashes = null)
    {
        foreach (var group in ConflictResultGrouping.GroupScripts(scripts.Collisions, changedPaths.Contains))
        {
            var modules = group.OrderBy(module => module.ModuleName, StringComparer.Ordinal).ToArray();
            var verdict = ConflictResultGrouping.GetScriptVerdict(modules);
            var (kind, severity, certainty) = verdict switch
            {
                ScriptModuleVerdict.Different => ("script_collision", "abnormal", "confirmed"),
                ScriptModuleVerdict.Identical => ("script_duplicate", "risk", "confirmed"),
                _ => ("script_collision", "incomplete", "possible"),
            };
            CountGroup(severity);
            var members = new List<ReportFile>();
            foreach (var occurrence in modules[0].Occurrences.DistinctBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                var file = await GetFileAsync(occurrence.ArchivePath, occurrence.ArchiveStamp);
                if (Visible(file.Path)) members.Add(file);
            }
            if (members.Count == 0) continue;
            groups.Add(new(kind, severity, new
            {
                kind, severity, certainty, modules[0].TopLevelName,
                files = members.Select(file => new
                {
                    file.Path, file.Sha256, file.Size, file.LastWriteTimeUtc, file.Ignored,
                    duplicateSha256 = duplicateHashes?.GetValueOrDefault(file.Path),
                }),
                modules = modules.Select(module => new
                {
                    module.ModuleName, module.Verdict, module.IsComplete,
                    entries = module.Occurrences.Where(occurrence => Visible(occurrence.ArchivePath)).Select(occurrence => new
                    {
                        path = occurrence.ArchivePath, occurrence.EntryName, occurrence.Format,
                        contentSha256 = occurrence.Sha256, issueCode = occurrence.Issue?.Code,
                    }),
                }),
            }, PathRank(members.Select(file => file.Path))));
        }
    }

    private void AddDuplicate(DuplicateGroup group)
    {
        CountGroup("risk");
        var members = new List<object>();
        foreach (var file in group.Files)
        {
            var member = AddFile(file.Path, group.Sha256, new(file.Size, file.LastWriteTimeUtc, file.Identity));
            if (Visible(file.Path))
                members.Add(new
                {
                    file.Path, file.Size, file.LastWriteTimeUtc, member.Sha256, member.Ignored,
                    file.SourceIds, file.PrimarySourceIds, file.IsInsideMods, file.DirectoryDepth, file.Identity,
                });
        }
        if (members.Count == 0) return;
        var kept = Visible(group.SuggestedKeepPath) ? group.SuggestedKeepPath : null;
        groups.Add(new("duplicate", "risk", new
        {
            kind = "duplicate", severity = "risk", certainty = "confirmed",
            group.Sha256, group.FileSize, group.Section, files = members,
            suggestedKeepPath = kept,
            suggestedDeletePaths = kept is null ? [] : group.SuggestedDeletePaths.Where(Visible).ToArray(),
            group.ReclaimableBytes,
        }, PathRank(group.Files.Select(file => file.Path))));
    }

    private void CountGroup(string severity)
    {
        groupCount++;
        groupSeverityCounts[severity]++;
        if (severity == "abnormal") abnormalGroupCount++;
    }

    private ReportFile AddFile(string path, string? sha256, FileStamp? stamp)
    {
        var file = new ReportFile(path, sha256, stamp?.Length, stamp?.LastWriteTimeUtc, ignoredPaths.Contains(path));
        files[path] = file;
        return file;
    }

    private async Task<ReportFile> GetFileAsync(string path, FileStamp? expected = null, bool reportHashFailure = true)
    {
        if (files.TryGetValue(path, out var file)) return file;
        try
        {
            var hash = await hasher.HashAsync(path);
            if (expected is null || hash.Stamp == expected)
                return AddFile(path, hash.Sha256, hash.Stamp);
            if (reportHashFailure)
                AddIssue(path, "report-file-version-changed", "生成报告时的文件版本与扫描时不一致", "hashing", isFile: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (reportHashFailure)
                AddIssue(path, "report-file-hash-failed", exception.Message, "hashing", isFile: true);
        }
        return AddFile(path, null, expected);
    }

    private void AddIssue(ScanIssue issue) => AddIssue(issue.Path, issue.Code, issue.Message, issue.Stage.ToString(),
        isFile: issue.Stage == ScanIssueStage.Hashing
            || issue.Stage == ScanIssueStage.Metadata && issue.Code is not ("entry-metadata-failed" or "source-metadata-failed"));

    private void AddIssue(string path, string code, string message, string stage, string? entry = null, bool isFile = false)
    {
        if (!issueKeys.Add((path.ToUpperInvariant(), code, entry))) return;
        var damaged = code is PackageReadIssueCode.TooSmallForHeader or PackageReadIssueCode.MagicMismatch
            or PackageReadIssueCode.IndexOutOfBounds or PackageReadIssueCode.IndexSizeMismatch
            or ScriptArchiveIssueCode.NotZip or ScriptArchiveIssueCode.Corrupt;
        issues.Add(new(path, code, message, stage, entry, isFile, damaged ? "damaged_file" : "read_incomplete",
            damaged ? "abnormal" : "incomplete"));
    }

    private static bool SamePath(string first, string second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);

    public string ToJson()
    {
        var outputGroups = OutputGroups.Select(group =>
        {
            var detail = JsonSerializer.SerializeToNode(group.Detail, JsonOptions)!.AsObject();
            if (options.OmitResourceKeys) detail.Remove("resourceKeys");
            return detail;
        }).ToArray();
        var visibleIssues = OutputIssues.Select(issue =>
        {
            var detail = JsonSerializer.SerializeToNode(new
            {
                issue.Path, issue.Code, issue.Message, issue.Stage, issue.Entry, issue.Kind, issue.Severity,
                certainty = "confirmed", file = issue.IsFile ? files[issue.Path] : null,
                ignored = issue.IsFile && ignoredPaths.Contains(issue.Path),
            }, JsonOptions)!.AsObject();
            if (issue.GroupingSize is { } size)
            {
                detail["reason"] = size.Reason;
                detail["resourceMatchCount"] = size.ResourceMatchCount;
                detail["collapsedResourceMatchCount"] = size.CollapsedResourceMatchCount;
                detail["resourceMatchLimit"] = size.ResourceMatchLimit;
                if (size.Reason == "duplicate-copies") detail["redundantCopyCount"] = size.RedundantCopyCount;
            }
            return detail;
        });
        var version = typeof(CliScanReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, version, command, sourceOrigin, notice, sources, startedAtUtc, completedAtUtc,
            discoveredFileCount, hashedFileCount, ignoredCount = IgnoredCount, incompleteCount = IncompleteCount,
            abnormalCount = AbnormalCount,
            groupCount, visibleGroupCount = outputGroups.Length, groups = outputGroups, issues = visibleIssues,
            complete = Complete, omittedGroupCount = OmittedGroupCount, omittedIssueCount = OmittedIssueCount,
            filters = new
            {
                severity = options.Severities, kind = options.Kinds, options.Limit, options.HideIgnored, options.PathContains,
                options.OmitResourceKeys, options.SummaryOnly,
            },
            severityCounts = SeverityCounts, issueCount = issues.Count,
            duplicateFileCount = duplicates?.DuplicateFileCount,
            reclaimableBytes = duplicates?.ReclaimableBytes,
        }, JsonOptions);
    }

    public string ToHtml()
    {
        var report = duplicates!;
        if (options.HideIgnored)
            report = report with
            {
                Groups = report.Groups.Where(group => Visible(group.SuggestedKeepPath))
                    .Select(group => group with { Files = group.Files.Where(file => Visible(file.Path)).ToArray() })
                    .Where(group => group.Files.Count >= 2).ToArray(),
                Issues = report.Issues.Where(issue => Visible(issue.Path)).ToArray(),
            };
        return DuplicateHtmlReportRenderer.Render(report);
    }

    private sealed record ReportGroup(string Kind, string Severity, object Detail, int PathRank);
    private sealed record ReportFile(string Path, string? Sha256, long? Size, DateTime? LastWriteTimeUtc, bool Ignored);
    private sealed record ReportIssue(string Path, string Code, string Message, string Stage, string? Entry,
        bool IsFile, string Kind, string Severity, ConflictGroupingSize? GroupingSize = null);
}
