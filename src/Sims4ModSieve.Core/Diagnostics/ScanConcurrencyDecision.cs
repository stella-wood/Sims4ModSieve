using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Diagnostics;

/// <summary>采用上限是配置值，不代表实际活跃任务峰值。</summary>
public sealed record ScanConcurrencyDecision(
    string Stage,
    DuplicateHashConcurrency Requested,
    int PolicyLimit,
    int AdoptedLimit,
    string StorageClassification,
    string? FallbackReason)
{
    public string DiagnosticText => $"阶段={Stage}；请求档位={(Requested == DuplicateHashConcurrency.Auto ? "Auto" : ((int)Requested).ToString())}；策略上限={PolicyLimit}；采用上限={AdoptedLimit}；存储分类={StorageClassification}；回退原因={FallbackReason ?? "无"}";

    internal static int Resolve(string stage, DuplicateHashConcurrency requested, IStorageProfile? storage,
        IEnumerable<string> participatingRoots, CancellationToken cancellationToken,
        Action<ScanConcurrencyDecision>? observed = null, int? explicitLimit = null, string? fallbackReason = null,
        int ssdAutoLimit = DuplicateHashConcurrencyPolicy.DuplicateHashSsdAutoLimit)
    {
        var roots = participatingRoots.Distinct(PathRules.Comparer).ToArray();
        var volumes = new List<StorageVolumeProfile>();
        if (requested == DuplicateHashConcurrency.Auto)
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                volumes.Add(storage?.GetVolume(root) ?? new(null, null, "未提供存储画像"));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var policy = DuplicateHashConcurrencyPolicy.Resolve(requested, volumes, ssdAutoLimit);
        var adopted = explicitLimit ?? policy;
        // 空阶段没有 worker，不制造实际并发记录。
        if (roots.Length > 0)
        {
            var classification = requested != DuplicateHashConcurrency.Auto ? "未探测（手动）"
                : string.Join("+", new[] { "SSD", "HDD", "未知" }.Where(kind => volumes.Any(volume =>
                    kind == (volume.IncursSeekPenalty switch { false => "SSD", true => "HDD", null => "未知" }))));
            var reason = fallbackReason ?? (explicitLimit.HasValue ? "显式低层并发优先"
                : requested == DuplicateHashConcurrency.Auto && volumes.Any(volume => volume.IncursSeekPenalty is null)
                    ? "存储类型未知，采用保守策略" : null);
            var decision = new ScanConcurrencyDecision(stage, requested, policy, adopted, classification, reason);
            ScanPerformance.RecordDiagnostic(decision.DiagnosticText);
            observed?.Invoke(decision);
        }
        return adopted;
    }
}
