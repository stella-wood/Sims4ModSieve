using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Desktop.Services;

public sealed record DuplicateRunSource(string Path, string Label);

public sealed record DuplicateRunInput(
    IReadOnlyList<DuplicateRunSource> Sources,
    string? ModsRoot = null)
{
    public DuplicateHashConcurrency HashConcurrency { get; init; }
    internal WindowsStorageProfile? StorageProfile { get; init; }
    internal IStableFileHasher? FileHasher { get; init; }
    internal ScanCoverage? Coverage { get; init; }
    internal Action<string>? ObserveFile { get; init; }
}

public interface IDuplicateRunService
{
    Task<DuplicateReport> RunAsync(
        DuplicateRunInput input,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class DuplicateRunService(DuplicateScanner scanner) : IDuplicateRunService
{
    private static readonly string[] IncludedExtensions = [".package", ".ts4script"];

    public Task<DuplicateReport> RunAsync(
        DuplicateRunInput input,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Sources);

        var sources = input.Sources
            .Select((source, index) => new ScanSource(
                $"目录 {index + 1}",
                source.Path,
                Order: index,
                Label: source.Label))
            .ToArray();

        var runScanner = input.FileHasher is { } hasher ? DuplicateScanner.CreateDefault(hasher) : scanner;
        return runScanner.ScanAsync(
            new DuplicateScanRequest(sources, input.ModsRoot, IncludedExtensions)
            {
                HashConcurrency = input.HashConcurrency,
                StorageProfile = input.StorageProfile ?? new WindowsStorageProfile(),
                Coverage = input.Coverage,
                ObserveFile = input.ObserveFile,
            },
            progress,
            cancellationToken);
    }
}
