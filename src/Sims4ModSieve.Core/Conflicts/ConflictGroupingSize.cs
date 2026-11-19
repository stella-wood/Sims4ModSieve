namespace Sims4ModSieve.Core.Conflicts;

public sealed record ConflictGroupingSize(long ResourceMatchCount, long CollapsedResourceMatchCount,
    long FilePairCount, long CollapsedFilePairCount, long MemoryBudgetBytes, int RedundantCopyCount)
{
    // 合成基准的方法、拟合残差和取值依据见 docs/architecture/冲突分组内存预算.md。
    public const long BytesPerFilePair = 492;
    public const long BytesPerResourceMatch = 124;
    public const decimal MemorySafetyMargin = 1.4m;
    // 其余 3/4 留给扫描报告、界面/CLI 输出、GC 与运行时，预算并非进程总内存上限。
    public const int MemoryShareDivisor = 4;
    // 沿用 256 MiB 的处理空间下限；低于 1 GiB 时会超过 1/4。
    public const long MinimumMemoryBudgetBytes = 256L * 1024 * 1024;

    public long AvailableMemoryBytes { get; init; }
    public bool IsLimitOverridden { get; init; }

    public static long CalculateMemoryBudgetBytes(long availableMemoryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableMemoryBytes);
        return Math.Max(MinimumMemoryBudgetBytes, availableMemoryBytes / MemoryShareDivisor);
    }

    public static long CalculateFilePairUpperBound(long resourceMatchCount, int fileCount)
    {
        return Math.Min(resourceMatchCount, fileCount * (fileCount - 1L) / 2);
    }

    public static long EstimateMemoryBytes(long filePairCount, long resourceMatchCount)
    {
        var estimate = decimal.Ceiling((BytesPerFilePair * (decimal)filePairCount
            + BytesPerResourceMatch * (decimal)resourceMatchCount) * MemorySafetyMargin);
        // 超出 long 的规模也须被默认预算拦截，不能因乘法溢出而放行。
        return (long)Math.Min(long.MaxValue, estimate);
    }

    public long EstimatedMemoryBytes => EstimateMemoryBytes(FilePairCount, ResourceMatchCount);
    public long CollapsedEstimatedMemoryBytes => EstimateMemoryBytes(CollapsedFilePairCount, CollapsedResourceMatchCount);
    // 保留 CLI issue 字段：固定本次 P 后换算的 M 上限，仅用于兼容输出，判定直接比较字节。
    public long ResourceMatchLimit => (long)Math.Max(0, decimal.Floor(
        (MemoryBudgetBytes / MemorySafetyMargin - BytesPerFilePair * (decimal)FilePairCount) / BytesPerResourceMatch));

    public const string IssueCode = "conflict-grouping-too-large";

    public bool ExceedsLimit => EstimatedMemoryBytes > MemoryBudgetBytes;
    public string? Reason => !ExceedsLimit ? null : CollapsedEstimatedMemoryBytes <= MemoryBudgetBytes
        ? "duplicate-copies" : "too-many-shared-resources";
    public string WarningMessage => Reason == "duplicate-copies"
        ? $"⚠️文件夹内存在大量相同副本（{RedundantCopyCount}份），建议优先进行重复检测"
        : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测";

    public string DiagnosticText => FormattableString.Invariant(
        $"冲突分组规模：资源匹配估算={ResourceMatchCount}；合并副本后估算={CollapsedResourceMatchCount}；上限={ResourceMatchLimit}；多余副本={RedundantCopyCount}；可用内存字节={AvailableMemoryBytes}；份额=1/{MemoryShareDivisor}；系数字节每匹配={BytesPerResourceMatch}；上限来源={(IsLimitOverridden ? "注入" : "内存预算")}；文件对上界={FilePairCount}；合并副本后文件对上界={CollapsedFilePairCount}；系数字节每文件对={BytesPerFilePair}；安全余量={MemorySafetyMargin}；估算内存字节={EstimatedMemoryBytes}；合并副本后估算内存字节={CollapsedEstimatedMemoryBytes}；内存预算字节={MemoryBudgetBytes}");
}

public sealed class ConflictGroupingTooLargeException(ConflictGroupingSize size) : Exception(size.WarningMessage)
{
    public ConflictGroupingSize Size { get; } = size;
}
