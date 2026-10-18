using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly CultureInfo TimingCulture = CultureInfo.GetCultureInfo("zh-CN");

    public string ScanTimingText { get; private set; } = "尚无扫描计时";
    public bool IsScanTimingEmpty => string.IsNullOrWhiteSpace(LatestScanTiming) || LatestScanTiming == "尚无扫描计时";

    private void UpdateScanTimingPresentation(string summary)
    {
        ScanTimingText = IsScanTimingEmpty ? "尚无扫描计时" : FormatScanTiming(summary);
        OnPropertyChanged(nameof(ScanTimingText));
        OnPropertyChanged(nameof(IsScanTimingEmpty));
    }

    private static string FormatScanTiming(string summary)
    {
        var header = Regex.Match(summary, @"^\[(?<date>[^\]]+)\] (?<title>[^\r\n]+)");
        var total = Regex.Match(summary, @"总耗时[:：]\s*(?<ms>\d+(?:\.\d+)?) ms");
        if (!header.Success || !total.Success || !TryTimingNumber(total.Groups["ms"].Value, out var totalMs))
            return summary;

        var stages = new List<(string Name, double Milliseconds, string Percent, Dictionary<string, long> Counts)>();
        var additional = new List<string>();
        Dictionary<string, long> scope = [];
        string? overhead = null;
        foreach (var line in summary.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('[') && line == header.Value) continue;
            if (line.StartsWith("总耗时", StringComparison.Ordinal)) continue;
            if (line.StartsWith("扫描范围：", StringComparison.Ordinal))
            {
                scope = ParseTimingCounts(line[5..]);
                continue;
            }
            if (line.StartsWith("阶段外调度/收尾：", StringComparison.Ordinal))
            {
                overhead = line.Replace("阶段外调度/收尾：", "调度与收尾：", StringComparison.Ordinal);
                continue;
            }
            var stage = Regex.Match(line, @"^(?<name>.+)：(?<ms>\d+(?:\.\d+)?) ms（(?<percent>\d+(?:\.\d+)?)%）；(?<counts>.*)$");
            if (stage.Success && TryTimingNumber(stage.Groups["ms"].Value, out var milliseconds)
                && TryTimingNumber(stage.Groups["percent"].Value, out _))
            {
                stages.Add((stage.Groups["name"].Value, milliseconds, stage.Groups["percent"].Value,
                    ParseTimingCounts(stage.Groups["counts"].Value)));
            }
            else additional.Add(line);
        }

        var text = new StringBuilder();
        var title = header.Groups["title"].Value.Split(" · ", 2, StringSplitOptions.TrimEntries);
        text.AppendLine("[扫描摘要]");
        text.AppendLine(title.Length == 2 ? $"类型：{title[0]}    状态：{title[1]}" : title[0]);
        var dateText = header.Groups["date"].Value;
        if (DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            dateText = date.ToString("yyyy-MM-dd HH:mm:ss zzz", TimingCulture);
        text.AppendLine($"开始：{dateText}");
        text.AppendLine($"总耗时：{(totalMs / 1000).ToString("F3", TimingCulture)} s（{totalMs.ToString("N3", TimingCulture)} ms）");
        text.AppendLine($"文件：{TimingCount(scope, "文件")} 个 / {FormatTimingBytes(scope.GetValueOrDefault("总字节"))}；索引资源：{TimingCount(scope, "索引资源条目")} 条");
        if (stages.Count > 0)
        {
            var slowest = stages.MaxBy(stage => stage.Milliseconds);
            text.AppendLine($"主要耗时：{slowest.Name}，{FormatTimingDuration(slowest.Milliseconds)}，占 {slowest.Percent}%");
        }
        text.AppendLine();
        text.AppendLine("[阶段计时：按执行顺序]");
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var values = stage.Counts;
            text.AppendLine($"{index + 1:D2}. {stage.Name}");
            text.AppendLine($"    耗时：{FormatTimingDuration(stage.Milliseconds)}（{stage.Percent}%）；并发上限：{TimingCount(values, "并发上限")}");
            var size = $"文件 {TimingCount(values, "文件")} 个；文件总大小 {FormatTimingBytes(values.GetValueOrDefault("输入字节"))}";
            if (values.GetValueOrDefault("资源/模块条目") > 0) size += $"；条目 {TimingCount(values, "资源/模块条目")} 条";
            text.AppendLine($"    规模：{size}");
            if (values.GetValueOrDefault("读取条目") > 0 || values.GetValueOrDefault("解压尝试") > 0
                || values.GetValueOrDefault("未完成条目") > 0)
            {
                text.AppendLine($"    读取 {TimingCount(values, "读取条目")} 条；解压尝试 {TimingCount(values, "解压尝试")} 条；解压成功 {TimingCount(values, "解压成功")} 条");
                text.AppendLine($"    未进入解压 {TimingCount(values, "未进入解压")} 条；未完成比较 {TimingCount(values, "未完成条目")} 条");
            }
            if (values.GetValueOrDefault("处理字节") > 0)
            {
                var basis = stage.Name.Contains("整文件 SHA-256", StringComparison.Ordinal) ? "成功哈希字节" : "原有预算消耗";
                text.AppendLine($"    处理量：{TimingCount(values, "处理字节")} bytes（{basis}）");
            }
            if (values.GetValueOrDefault("跳过文件") > 0) text.AppendLine($"    跳过文件：{TimingCount(values, "跳过文件")} 个");
            if (values.GetValueOrDefault("结果项") > 0) text.AppendLine($"    结果项：{TimingCount(values, "结果项")}");
        }
        if (overhead is not null) text.AppendLine(overhead);
        text.AppendLine();
        text.AppendLine("[机器与统计口径]");
        foreach (var line in additional) text.AppendLine(line);
        if (scope.ContainsKey("候选比较条目"))
            text.AppendLine($"资源内容候选：{TimingCount(scope, "候选比较条目")} 条；未进入候选比较：{TimingCount(scope, "未进入候选比较")} 条");
        text.AppendLine("未进入解压包含未压缩条目与提前跳过的条目，不等于比较失败。");
        return text.ToString();
    }

    private static string TimingCount(IReadOnlyDictionary<string, long> values, string key) =>
        values.GetValueOrDefault(key).ToString("N0", TimingCulture);

    private static Dictionary<string, long> ParseTimingCounts(string text)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in text.Split('；'))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                counts[parts[0]] = value;
        }
        return counts;
    }

    private static bool TryTimingNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string FormatTimingDuration(double milliseconds) => milliseconds >= 1000
        ? (milliseconds / 1000).ToString("F3", TimingCulture) + " s"
        : milliseconds.ToString("F3", TimingCulture) + " ms";

    private static string FormatTimingBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "N0" : "F2", TimingCulture) + " " + units[unit];
    }
}
