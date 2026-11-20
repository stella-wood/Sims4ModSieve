using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using static Sims4ModSieve.Core.Tests.Conflicts.CandidateFactory;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ResourceContentReusePolicyTests
{
    [TestMethod]
    [DataRow(ResourceContentReuseMode.Off, DuplicateHashConcurrency.Eight, false, false)]
    [DataRow(ResourceContentReuseMode.On, DuplicateHashConcurrency.Auto, true, false)]
    [DataRow(ResourceContentReuseMode.On, DuplicateHashConcurrency.Auto, null, false)]
    [DataRow(ResourceContentReuseMode.Auto, DuplicateHashConcurrency.Eight, null, false)]
    [DataRow(ResourceContentReuseMode.Auto, DuplicateHashConcurrency.Eight, true, false)]
    [DataRow(ResourceContentReuseMode.Auto, DuplicateHashConcurrency.Eight, false, true)]
    [DataRow(ResourceContentReuseMode.On, DuplicateHashConcurrency.Eight, null, true)]
    [DataRow(ResourceContentReuseMode.On, DuplicateHashConcurrency.Auto, false, true)]
    public async Task StorageAndModeGateRawReads(ResourceContentReuseMode mode, DuplicateHashConcurrency concurrency, bool? seek, bool enabled)
    {
        var hasher = new BatchHasher();
        ResourceContentReuseDiagnostics? diagnostic = null;
        using var performance = ScanPerformance.Start("policy");
        var result = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), ("a.package", 0), ("b.package", 0))])
        {
            Options = new(concurrency, new Storage(seek)) { ContentReuse = mode, ContentReuseObserved = value => diagnostic = value },
        });
        Assert.AreEqual(enabled ? 1 : 0, hasher.ReuseCalls);
        Assert.IsTrue(result.Comparisons.Single().IsComplete);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(0L, diagnostic.SizeSkipped);
        StringAssert.Contains(performance.Finish("ok", "test"), diagnostic.DiagnosticText);
        Assert.IsTrue(enabled || diagnostic.BypassReason is not null);
    }

    [TestMethod]
    public async Task FiniteBudgetAndOldCallsAlwaysKeepOriginalPath()
    {
        var hasher = new BatchHasher();
        var candidates = new[] { Candidate(Key(), ("a.package", 0), ("b.package", 0)) };
        await new ResourceContentComparer(hasher).CompareAsync(new(candidates));
        await new ResourceContentComparer(hasher).CompareAsync(new(candidates)
        {
            Options = new(DuplicateHashConcurrency.Eight) { ContentReuse = ResourceContentReuseMode.On },
            Limits = ResourceContentLimits.Default with { MaxTotalProcessedBytes = 1, MaxDegreeOfParallelism = 3 },
        });
        Assert.AreEqual(0, hasher.ReuseCalls);
        Assert.AreEqual(ResourceContentReuseMode.Off, new ConflictScanOptions().ContentReuse);
        Assert.AreEqual(3, hasher.LastParallelism);
    }

    [TestMethod]
    [DataRow("count")] [DataRow("target")] [DataRow("path")]
    public async Task BatchContractIsValidatedBeforePublishing(string corruption)
    {
        var hasher = new BatchHasher { Corruption = corruption };
        var report = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), ("a.package", 0), ("b.package", 0))])
        { Options = new(DuplicateHashConcurrency.Two) { ContentReuse = ResourceContentReuseMode.On } });
        Assert.IsFalse(report.Comparisons.Single().IsComplete);
        Assert.IsTrue(report.Comparisons.SelectMany(item => item.Uncompared).All(item => item.Issue?.Code == ResourceContentIssueCode.UnexpectedError));
    }

    private sealed class Storage(bool? seek) : IStorageProfile
    { public StorageVolumeProfile GetVolume(string directoryPath) => new("volume", seek); }

    private sealed class BatchHasher : IResourceContentHasher
    {
        internal int ReuseCalls;
        internal int LastParallelism;
        internal string? Corruption;
        public Task<ResourceContentBatchResult> HashAsync(ResourceContentBatchRequest request, ResourceContentBudget budget, CancellationToken cancellationToken = default)
        {
            LastParallelism = request.Limits.MaxDegreeOfParallelism;
            return Task.FromResult(new ResourceContentBatchResult(request.PackagePath, request.ExpectedStamp,
                request.Targets.Select(target => ResourceContentResult.Success(target, new string('A', 64), 1)).ToArray()));
        }
        public async Task<IReadOnlyList<ResourceContentBatchResult>?> TryHashBatchesWithReuseAsync(IReadOnlyList<ResourceContentBatchRequest> requests,
            ResourceContentBudget budget, Action<ResourceContentReuseDiagnostics> observed, CancellationToken cancellationToken = default)
        {
            ReuseCalls++;
            observed(new() { SavedDecompressions = 1 });
            var results = new List<ResourceContentBatchResult>();
            foreach (var request in requests)
            {
                var result = await HashAsync(request, budget, cancellationToken);
                results.Add(Corruption switch
                {
                    "target" => result with { Results = [ResourceContentResult.Success(request.Targets[0] with { Ordinal = 99 }, new string('A', 64), 1)] },
                    "path" => result with { PackagePath = "wrong.package" },
                    _ => result,
                });
            }
            return Corruption == "count" ? [] : results;
        }
    }
}
