using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Conflicts;

/// <summary>扫描开始时固定设置；存储画像只在协调线程、实际读取前解析。</summary>
public sealed record ConflictScanOptions(
    DuplicateHashConcurrency Concurrency = DuplicateHashConcurrency.Auto,
    IStorageProfile? StorageProfile = null)
{
    public Action<ScanConcurrencyDecision>? ConcurrencyDecided { get; init; }
}
