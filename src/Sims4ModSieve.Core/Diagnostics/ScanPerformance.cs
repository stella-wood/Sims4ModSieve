using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Sims4ModSieve.Core.Diagnostics;

// 随 ExecutionContext 传递，避免为性能观测修改扫描器、读取器和报告的业务契约。
public sealed class ScanPerformance(string scanKind) : IDisposable
{
    private static readonly AsyncLocal<ScanPerformance?> Ambient = new();
    private static readonly AsyncLocal<Stage?> ActiveStage = new();
    private readonly ScanPerformance? previous = Ambient.Value;
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly List<Stage> stages = [];
    private readonly DateTimeOffset startedAt = DateTimeOffset.Now;

    public static ScanPerformance Start(string scanKind)
    {
        var performance = new ScanPerformance(scanKind);
        Ambient.Value = performance;
        return performance;
    }

    public static Stage? Measure(string name, int parallelism = 1)
    {
        var owner = Ambient.Value;
        if (owner is null) return null;
        var stage = new Stage(owner, name, parallelism, ActiveStage.Value);
        ActiveStage.Value = stage;
        return stage;
    }

    public static Stage? CurrentStage => ActiveStage.Value;
    public IReadOnlyList<Stage> Stages => stages;

    public string Finish(string outcome, string storage)
    {
        total.Stop();
        var text = new StringBuilder();
        text.AppendLine($"[{startedAt:O}] {scanKind} · {outcome}");
        text.AppendLine(FormattableString.Invariant($"总耗时：{total.Elapsed.TotalMilliseconds:F3} ms"));
        text.AppendLine($"机器：逻辑 CPU {Environment.ProcessorCount}；{RuntimeInformation.OSDescription}；{RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"CPU：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知"}");
        text.AppendLine($"存储：{storage}");
        var discovery = stages.LastOrDefault(stage => stage.Name == "整文件核对 · 枚举目录和文件信息")
            ?? stages.FirstOrDefault(stage => stage.Name.EndsWith("枚举目录和文件信息", StringComparison.Ordinal));
        var index = stages.FirstOrDefault(stage => stage.Name == "Package · 排序和读取索引");
        var content = stages.FirstOrDefault(stage => stage.Name == "资源内容 · 复核索引、读取、解压和哈希");
        text.AppendLine($"扫描范围：文件={discovery?.Files ?? 0}；总字节={discovery?.Bytes ?? 0}；索引资源条目={index?.Entries ?? 0}；候选比较条目={content?.Entries ?? 0}；未进入候选比较={Math.Max(0, (index?.Entries ?? 0) - (content?.Entries ?? 0))}");
        text.AppendLine($"解压合计：尝试={stages.Sum(stage => stage.DecompressionAttempts)}；成功={stages.Sum(stage => stage.DecompressionCompleted)}；原始字节相同跳过=0（当前流程无此捷径）");
        text.AppendLine("口径：阶段为墙钟耗时，并发为配置上限；输入字节为涉及文件大小，处理字节为成功哈希字节或内容预算消耗，并非磁盘实际读取量。非内容阶段解压计数为 0；取消时规模可能尚未汇齐。");
        foreach (var stage in stages)
        {
            var percent = total.Elapsed.Ticks == 0 ? 0 : stage.Elapsed.Ticks * 100d / total.Elapsed.Ticks;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{stage.Name}：{stage.Elapsed.TotalMilliseconds:F3} ms（{percent:F2}%）；并发上限={stage.Parallelism}；文件={stage.Files}；输入字节={stage.Bytes}；资源/模块条目={stage.Entries}；解压尝试={stage.DecompressionAttempts}；解压成功={stage.DecompressionCompleted}；未进入解压={(stage.CountsContent ? Math.Max(0, stage.Entries - stage.DecompressionAttempts) : 0)}；读取条目={stage.ReadEntries}；处理字节={stage.ProcessedBytes}；跳过文件={stage.SkippedFiles}；未完成条目={stage.FailedEntries}；结果项={stage.ResultItems}"));
        }
        var measured = stages.Sum(stage => stage.Elapsed.Ticks);
        text.AppendLine(FormattableString.Invariant($"阶段外调度/收尾：{Math.Max(0, total.Elapsed.Ticks - measured) / (double)TimeSpan.TicksPerMillisecond:F3} ms"));
        return text.ToString();
    }

    public void Dispose() => Ambient.Value = previous;

    public sealed class Stage(ScanPerformance owner, string name, int parallelism, Stage? previous) : IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private long readEntries;
        private long decompressionAttempts;
        private long decompressionCompleted;
        private long observedEntries;
        private bool disposed;

        public string Name { get; } = name;
        public int Parallelism { get; } = parallelism;
        public TimeSpan Elapsed { get; private set; }
        public long Files { get; set; }
        public long Bytes { get; set; }
        public long Entries { get; set; }
        public long ProcessedBytes { get; set; }
        public long SkippedFiles { get; set; }
        public long FailedEntries { get; set; }
        public long ResultItems { get; set; }
        public bool CountsContent { get; set; }
        public long ReadEntries => Interlocked.Read(ref readEntries);
        public long DecompressionAttempts => Interlocked.Read(ref decompressionAttempts);
        public long DecompressionCompleted => Interlocked.Read(ref decompressionCompleted);
        public long ObservedEntries => Interlocked.Read(ref observedEntries);

        public void ObserveEntries(long count) => Interlocked.Add(ref observedEntries, count);
        public void BeginContentRead(bool compressed)
        {
            Interlocked.Increment(ref readEntries);
            if (compressed) Interlocked.Increment(ref decompressionAttempts);
        }
        public void CompleteDecompression() => Interlocked.Increment(ref decompressionCompleted);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            watch.Stop();
            Elapsed = watch.Elapsed;
            owner.stages.Add(this);
            ActiveStage.Value = previous;
        }
    }
}
