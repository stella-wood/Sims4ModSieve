using System.Runtime.InteropServices;
using System.Text.Json;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Benchmarks;

internal static class Program
{
    private static Task<int> Main(string[] args) => ExecuteAsync(args);

    internal static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("generate --output <new-corpus-directory>");
            Console.WriteLine("run --source <directory> [--source <directory> ...] --output <new-directory>");
            Console.WriteLine("    --sample-id <id> [--runs 3] [--mods-root <directory>] [--baseline <previous-output>]");
            Console.WriteLine("    [--cancel-after-ms <milliseconds>] [--revision <commit>] [--storage <description>]");
            Console.WriteLine("    [--scope all|duplicates] (default: all)");
            Console.WriteLine("    [--hash-concurrency auto|1|2|4|8] (default: auto)");
            return 0;
        }
        try
        {
            var options = Parse(args);
            if (args[0] == "generate")
            {
                if (options.Keys.Any(key => key != "--output")) throw new ArgumentException("generate only accepts --output.");
                BenchmarkInputs.Generate(One(options, "--output", required: true)!);
                return 0;
            }
            if (args[0] != "run") throw new ArgumentException("Unknown command.");
            return await RunAsync(options);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> RunAsync(Dictionary<string, List<string>> options)
    {
        if (!options.TryGetValue("--source", out var paths) || paths.Count == 0)
            throw new ArgumentException("At least one --source is required.");
        var sources = paths.Select((path, index) => new ScanSource($"source-{index + 1}", Path.GetFullPath(path), Order: index)).ToArray();
        var output = Path.GetFullPath(One(options, "--output", required: true)!);
        var sampleId = One(options, "--sample-id", required: true)!;
        var runs = PositiveInt(options, "--runs") ?? 3;
        if (runs > 100) throw new ArgumentException("--runs must be at most 100.");
        var cancelAfter = PositiveInt(options, "--cancel-after-ms");
        var scope = One(options, "--scope") ?? "all";
        if (scope is not ("all" or "duplicates")) throw new ArgumentException("--scope must be all or duplicates.");
        var concurrencyLabel = One(options, "--hash-concurrency") ?? "auto";
        var hashConcurrency = concurrencyLabel switch
        {
            "auto" => DuplicateHashConcurrency.Auto,
            "1" => DuplicateHashConcurrency.One,
            "2" => DuplicateHashConcurrency.Two,
            "4" => DuplicateHashConcurrency.Four,
            "8" => DuplicateHashConcurrency.Eight,
            _ => throw new ArgumentException("--hash-concurrency must be auto, 1, 2, 4 or 8."),
        };
        BenchmarkInputs.ValidateOutput(output, sources.Select(source => source.Path));
        var before = InputManifest.Capture(sources.Select(source => source.Path));
        string? expected = null;
        if (One(options, "--baseline") is { } baseline)
        {
            expected = Path.GetFullPath(Path.Combine(baseline, "snapshot.json"));
            var previous = JsonSerializer.Deserialize<BaselineHeader>(
                await File.ReadAllTextAsync(Path.Combine(baseline, "summary.json")), ResultSnapshot.JsonOptions);
            if (previous is null || !previous.Valid || previous.CancelledRuns != 0
                || previous.SchemaVersion != 1 || previous.SampleId != sampleId
                || previous.ManifestFingerprint != before.Fingerprint || (previous.Scope ?? "all") != scope)
                throw new ArgumentException("Baseline scope, sample ID or manifest differs; freeze the same corpus and sources.");
            ResultSnapshot.Validate(expected);
        }
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), before);
        var measurements = new List<RunMeasurement>();
        string? difference = null;
        var stable = true;
        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            for (var iteration = 1; iteration <= runs; iteration++)
            {
                var snapshotPath = Path.Combine(output, $"snapshot-{iteration:D2}.json");
                var storage = new WindowsStorageProfile();
                var measurement = await BenchmarkRunner.MeasureAsync(
                    token => BenchmarkRunner.CaptureAsync(sources, One(options, "--mods-root"), token, snapshotPath, scope,
                        hashConcurrency, storage),
                    cancelAfter, lifetime.Token, storage);
                measurements.Add(measurement);
                await File.WriteAllTextAsync(Path.Combine(output, $"run-{iteration:D2}.log"), measurement.TimingLog);
                await WriteJsonAsync(Path.Combine(output, $"run-{iteration:D2}.json"), measurement);
                if (measurement.Snapshot is { } actual)
                {
                    expected ??= actual;
                    difference ??= ResultSnapshot.FirstDifference(expected, actual);
                }
                else if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                stable &= before.Fingerprint == InputManifest.Capture(sources.Select(source => source.Path)).Fingerprint;
                Console.WriteLine($"Run {iteration}: {measurement.Outcome}, {measurement.Milliseconds:F3} ms");
                if (measurement.Error is not null) Console.Error.WriteLine(measurement.Error);
                if (measurement.Outcome == "failed") break;
                if (lifetime.IsCancellationRequested || !stable || difference is not null) break;
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
        var completed = measurements.Where(run => run.Outcome == "completed").ToArray();
        if (!cancelAfter.HasValue && stable && difference is null && completed.Length == runs && expected is not null)
            File.Copy(expected, Path.Combine(output, "snapshot.json"));
        var valid = stable && difference is null && measurements.Count == runs
            && (cancelAfter.HasValue ? measurements.All(run => run.Outcome == "cancelled") : completed.Length == runs);
        await WriteJsonAsync(Path.Combine(output, "summary.json"), new
        {
            SchemaVersion = 1, Scope = scope, SampleId = sampleId, ManifestFingerprint = before.Fingerprint,
            FileCount = before.Files.Count, TotalBytes = before.Files.Sum(file => file.Stamp.Length),
            Revision = One(options, "--revision") ?? "unspecified",
            Storage = One(options, "--storage") ?? "unknown",
            HashConcurrency = concurrencyLabel,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription, LogicalCpuCount = Environment.ProcessorCount,
            Runs = measurements.Count, RequestedRuns = runs, CompletedRuns = completed.Length,
            CancelledRuns = measurements.Count(run => run.Outcome == "cancelled"),
            StableInput = stable, Difference = difference, Valid = valid,
            MedianMilliseconds = completed.Length == 0 ? (double?)null : BenchmarkRunner.Median(completed.Select(run => run.Milliseconds)),
            StageMedians = completed.SelectMany(run => run.Stages).Select(stage => stage.Name).Distinct()
                .Select(name => new
                {
                    Name = name,
                    Milliseconds = BenchmarkRunner.Median(completed.SelectMany(run => run.Stages).Where(stage => stage.Name == name).Select(stage => stage.Milliseconds)),
                }),
            CancellationMedianMs = measurements.Any(run => run.CancellationLatencyMs is not null)
                ? (double?)BenchmarkRunner.Median(measurements.Where(run => run.CancellationLatencyMs is not null).Select(run => run.CancellationLatencyMs!.Value))
                : null,
            Cache = "disabled", OsPageCache = "uncontrolled",
            UiAndTrueDiskBaseline = "not measured; collect on Windows",
            MetricNotes = "Total includes streaming snapshot serialization and its file write; working set is process lifetime peak; handles are start/end observations, not a peak.",
        });
        if (difference is not null) Console.Error.WriteLine(difference);
        if (!stable) Console.Error.WriteLine("Input manifest changed; measurements are invalid.");
        if (cancelAfter.HasValue && completed.Length > 0)
            Console.Error.WriteLine("Scan completed before cancellation; cancellation was not verified for those runs.");
        return valid ? 0 : 1;
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        await using var stream = File.Create(path);
        if (value is not RunMeasurement measurement)
        {
            await JsonSerializer.SerializeAsync(stream, value, ResultSnapshot.JsonOptions);
            return;
        }
        // Keep the existing embedded snapshot without parsing or buffering the whole file again.
        var header = JsonSerializer.SerializeToUtf8Bytes(measurement, ResultSnapshot.JsonOptions);
        await stream.WriteAsync(header.AsMemory(0, header.Length - 1));
        await stream.WriteAsync(",\n  \"snapshot\": "u8.ToArray());
        if (measurement.Snapshot is { } snapshotPath)
        {
            await using var snapshot = File.OpenRead(snapshotPath);
            await snapshot.CopyToAsync(stream);
        }
        else await stream.WriteAsync("null"u8.ToArray());
        await stream.WriteAsync("\n}"u8.ToArray());
    }

    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        string[] allowed = ["--source", "--output", "--sample-id", "--runs", "--mods-root", "--baseline",
            "--cancel-after-ms", "--revision", "--storage", "--scope", "--hash-concurrency"];
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!allowed.Contains(key) || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid option or missing value: {key}");
            if (!options.TryGetValue(key, out var values)) options[key] = values = [];
            if (key != "--source" && values.Count > 0) throw new ArgumentException($"Repeated option: {key}");
            values.Add(args[index + 1]);
        }
        return options;
    }

    private static string? One(Dictionary<string, List<string>> options, string key, bool required = false) =>
        options.TryGetValue(key, out var values) ? values.Single()
        : required ? throw new ArgumentException($"Missing {key}.") : null;

    private static int? PositiveInt(Dictionary<string, List<string>> options, string key)
    {
        if (One(options, key) is not { } value) return null;
        if (!int.TryParse(value, out var number) || number < 1) throw new ArgumentException($"{key} must be positive.");
        return number;
    }

    private sealed record BaselineHeader(bool Valid, int CancelledRuns, int SchemaVersion,
        string? SampleId, string? ManifestFingerprint, string? Scope);
}
