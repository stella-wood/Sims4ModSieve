using System.IO;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed record ConflictFileRow(string Path, string? Reason = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record ConflictContentRow(string Label, IReadOnlyList<ConflictFileRow> Files, string? Sha256 = null);

public sealed record ConflictGroupRow(
    string Title,
    string Detail,
    IReadOnlyList<ConflictContentRow> Contents,
    bool IsDifferent,
    bool IsIdentical,
    bool IsPartial)
{
    public string StateText => string.Join(" · ", new[]
    {
        IsDifferent ? "内容不同" : null,
        IsIdentical ? "内容相同" : null,
        IsPartial ? "没比完" : null,
    }.Where(value => value is not null));
}

public sealed record ConflictScriptGroupRow(string Name, IReadOnlyList<ConflictGroupRow> Modules)
{
    public string StateSummary => $"{Modules.Count} 个模块 · 内容不同 {Modules.Count(module => module.IsDifferent)} · 内容相同 {Modules.Count(module => module.IsIdentical)} · 没比完 {Modules.Count(module => module.IsPartial)}";
    public int FileCount => Modules.SelectMany(module => module.Contents)
        .SelectMany(content => content.Files)
        .Select(file => file.Path)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed record ConflictProblemReason(string Message, string Code, string? EntryName, string? Detail)
{
    public string Display => EntryName is null ? Message : $"{EntryName}：{Message}";
    public string Technical => Detail is null ? Code : $"{Code} · {Detail}";
}

public sealed record ConflictProblemRow(string Path, bool IsWholeFile, IReadOnlyList<ConflictProblemReason> Reasons)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Scope => IsWholeFile ? "整个文件未分析" : "部分内容没比完";
}

public sealed record ConflictRangeProblem(string Path, string Message, string Code, string? Detail);

public sealed class ConflictResultSession
{
    public IReadOnlyList<ConflictGroupRow> Resources { get; private set; } = [];
    public IReadOnlyList<ConflictScriptGroupRow> Scripts { get; private set; } = [];
    public IReadOnlyList<ConflictProblemRow> Problems { get; private set; } = [];
    public IReadOnlyList<ConflictRangeProblem> RangeProblems { get; private set; } = [];

    public void Reset()
    {
        Resources = [];
        Scripts = [];
        Problems = [];
        RangeProblems = [];
    }

    public void Replace(PackageConflictScanReport packages, ResourceContentComparisonReport contents, ScriptModuleScanReport scripts)
    {
        Resources = contents.Comparisons.Select((comparison, index) => new ConflictGroupRow(
            $"资源组 {index + 1}",
            $"TGI {comparison.Key}",
            comparison.ContentGroups.Select((group, groupIndex) => new ConflictContentRow(
                $"内容 {groupIndex + 1}",
                group.Occurrences.Select(occurrence => new ConflictFileRow(occurrence.PackagePath)).ToArray(), group.Sha256))
                .Concat(comparison.Uncompared.Count == 0 ? [] :
                    [new ConflictContentRow("未完成比较", comparison.Uncompared.Select(occurrence =>
                        new ConflictFileRow(occurrence.PackagePath, occurrence.Issue?.Message)).ToArray())]).ToArray(),
            comparison.Verdict == ResourceContentVerdict.Different,
            comparison.Verdict == ResourceContentVerdict.Identical,
            !comparison.IsComplete)).ToArray();

        Scripts = scripts.Collisions.GroupBy(collision => collision.TopLevelName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ConflictScriptGroupRow(group.Key, group.Select(collision => new ConflictGroupRow(
                collision.ModuleName,
                $"{collision.ArchiveCount} 个文件",
                collision.ContentGroups.Select((content, index) => new ConflictContentRow(
                    $"内容 {index + 1}",
                    content.Occurrences.Select(occurrence => new ConflictFileRow(occurrence.ArchivePath)).ToArray(), content.Sha256))
                    .Concat(collision.Uncompared.Count == 0 ? [] :
                        [new ConflictContentRow("未完成比较", collision.Uncompared.Select(occurrence =>
                            new ConflictFileRow(occurrence.ArchivePath, occurrence.Issue?.Message)).ToArray())]).ToArray(),
                collision.Verdict == ScriptModuleVerdict.Different,
                collision.Verdict == ScriptModuleVerdict.Identical,
                !collision.IsComplete)).ToArray())).ToArray();

        var problems = new Dictionary<string, (bool Whole, List<ConflictProblemReason> Reasons)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool whole, ConflictProblemReason reason)
        {
            if (!problems.TryGetValue(path, out var entry))
            {
                entry = (false, []);
            }
            entry.Whole |= whole;
            if (!entry.Reasons.Contains(reason)) entry.Reasons.Add(reason);
            problems[path] = entry;
        }

        foreach (var path in packages.IncompletePackagePaths)
            Add(path, true, new("package 索引未完成读取", "package-index-incomplete", null, null));
        foreach (var comparison in contents.Comparisons)
            foreach (var occurrence in comparison.Uncompared)
                if (occurrence.Issue is { } issue)
                    Add(occurrence.PackagePath, false, new(issue.Message, issue.Code, comparison.Key.ToString(), issue.Detail));
        foreach (var path in scripts.IncompleteArchivePaths)
            Add(path, true, new("脚本归档未完成读取", "script-archive-incomplete", null, null));

        var rangeProblems = new List<ConflictRangeProblem>();
        foreach (var issue in packages.Issues)
        {
            if (issue.Stage is PackageConflictScanIssueStage.SourceValidation or PackageConflictScanIssueStage.Discovery
                || issue.Code is "entry-metadata-failed" or "source-metadata-failed")
                rangeProblems.Add(new(issue.Path, issue.Message, issue.Code, issue.Detail));
            else Add(issue.Path, true,
                new(issue.Message, issue.Code, null, issue.Detail));
        }
        foreach (var issue in scripts.Issues)
        {
            if (issue.Stage is ScriptArchiveIssueStage.SourceValidation or ScriptArchiveIssueStage.Discovery
                || issue.Code is "entry-metadata-failed" or "source-metadata-failed")
                rangeProblems.Add(new(issue.Path, issue.Message, issue.Code, issue.Detail));
            else Add(issue.Path, issue.EntryName is null,
                new(issue.Message, issue.Code, issue.EntryName, issue.Detail));
        }

        Problems = problems.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ConflictProblemRow(entry.Key, entry.Value.Whole, entry.Value.Reasons.ToArray())).ToArray();
        RangeProblems = rangeProblems.Distinct().ToArray();
    }
}
