using System.Diagnostics;
using System.Text.Json.Nodes;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Benchmarks;

internal sealed record StageMeasurement(string Name, double Milliseconds, int Parallelism,
    long Files, long Bytes, long Entries, long ProcessedBytes, long FailedEntries,
    long ReadEntries, long DecompressionAttempts, long DecompressionCompleted);

internal sealed record RunMeasurement(string Outcome, string? Error, double Milliseconds, double? CancellationLatencyMs,
    long AllocatedBytes, long ProcessLifetimePeakWorkingSetBytes, int? HandlesBefore, int? HandlesAfter,
    IReadOnlyList<StageMeasurement> Stages, string TimingLog, JsonObject? Snapshot);

internal static class BenchmarkRunner
{
    internal static async Task<JsonObject> CaptureAsync(IReadOnlyList<ScanSource> sources, string? modsRoot,
        CancellationToken cancellationToken)
    {
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(),
            LlamaLogicPackageIndexReader.CreateDefault()).ScanAsync(new(sources), cancellationToken);
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault())
            .CompareAsync(new(packages.Candidates), cancellationToken: cancellationToken);
        var scripts = await new ScriptModuleScanner(new PhysicalFileSystemAccess())
            .ScanAsync(new(sources), cancellationToken: cancellationToken);
        var duplicates = await DuplicateScanner.CreateDefault().ScanAsync(
            new(sources, modsRoot, [".package", ".ts4script"]), cancellationToken);
        PackageConflictGroupingResult grouping;
        using (ScanPerformance.Measure("基准 · 文件关系构建（无 WPF）"))
        {
            var builder = new PackageConflictGroupingBuilder();
            grouping = builder.Build(builder.Prepare(packages, contents, cancellationToken: cancellationToken),
                new(), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (ScanPerformance.Measure("基准 · 生成一致性快照"))
            return ResultSnapshot.Create(duplicates, packages, contents, scripts, grouping);
    }

    internal static async Task<RunMeasurement> MeasureAsync(
        Func<CancellationToken, Task<JsonObject>> capture, int? cancelAfterMs = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = new CancellationTokenSource();
        long requestedAt = 0;
        void RequestCancellation()
        {
            Interlocked.CompareExchange(ref requestedAt, Stopwatch.GetTimestamp(), 0);
            linked.Cancel();
        }
        using var registration = cancellationToken.Register(RequestCancellation);
        using var timer = cancelAfterMs is { } delay
            ? new Timer(_ => RequestCancellation(), null, delay, Timeout.Infinite)
            : null;
        using var process = Process.GetCurrentProcess();
        var handlesBefore = TryHandleCount(process);
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var started = Stopwatch.GetTimestamp();
        using var performance = ScanPerformance.Start("后台基准（不含 WPF）");
        JsonObject? snapshot = null;
        var outcome = "completed";
        string? error = null;
        try
        {
            // Discovery contains synchronous work; the timer must be able to cancel that worker too.
            snapshot = await Task.Run(() => capture(linked.Token), CancellationToken.None).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            outcome = "cancelled";
            snapshot = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = "failed";
            error = $"{exception.GetType().Name}: {exception.Message}";
            snapshot = null;
        }
        finally
        {
            if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
        }
        var stopped = Stopwatch.GetTimestamp();
        var allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore;
        var timingLog = performance.Finish(outcome, "未自动探测；由本地基准记录存储介质");
        var requested = Interlocked.Read(ref requestedAt);
        var stages = performance.Stages.Select(stage => new StageMeasurement(stage.Name,
            stage.Elapsed.TotalMilliseconds, stage.Parallelism, stage.Files, stage.Bytes, stage.Entries,
            stage.ProcessedBytes, stage.FailedEntries, stage.ReadEntries,
            stage.DecompressionAttempts, stage.DecompressionCompleted)).ToArray();
        process.Refresh();
        return new RunMeasurement(outcome, error, Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds,
            requested == 0 || outcome != "cancelled" ? null : Stopwatch.GetElapsedTime(requested, stopped).TotalMilliseconds,
            allocatedBytes, process.PeakWorkingSet64,
            handlesBefore, TryHandleCount(process), stages, timingLog, snapshot);
    }

    internal static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) throw new ArgumentException("No completed measurements.", nameof(values));
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static int? TryHandleCount(Process process)
    {
        try { process.Refresh(); return process.HandleCount; }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return null; }
    }
}
