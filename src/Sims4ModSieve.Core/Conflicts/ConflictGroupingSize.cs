namespace Sims4ModSieve.Core.Conflicts;

public sealed record ConflictGroupingSize(long ResourceMatchCount, long CollapsedResourceMatchCount,
    long ResourceMatchLimit, int RedundantCopyCount)
{
    // 合成基准的方法、原始数据和取值依据见 docs/architecture/冲突分组内存预算.md。
    public const long BytesPerResourceMatch = 1024;
    // 其余 3/4 留给扫描报告、界面/CLI 输出、GC 与运行时，预算并非进程总内存上限。
    public const int MemoryShareDivisor = 4;
    // 小内存环境至少保留 26 万匹配的处理空间（按系数计 256 MiB）；低于 1 GiB 时会超过 1/4。
    public const long MinimumResourceMatchLimit = 262_144;

    public long AvailableMemoryBytes { get; init; }
    public bool IsLimitOverridden { get; init; }

    public static long CalculateResourceMatchLimit(long availableMemoryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableMemoryBytes);
        return Math.Max(MinimumResourceMatchLimit, availableMemoryBytes / MemoryShareDivisor / BytesPerResourceMatch);
    }

    public const string IssueCode = "conflict-grouping-too-large";

    public bool ExceedsLimit => ResourceMatchCount > ResourceMatchLimit;
    public string? Reason => !ExceedsLimit ? null : CollapsedResourceMatchCount <= ResourceMatchLimit
        ? "duplicate-copies" : "too-many-shared-resources";
    public string WarningMessage => Reason == "duplicate-copies"
        ? $"⚠️文件夹内存在大量相同副本（{RedundantCopyCount}份），建议优先进行重复检测"
        : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测";

    public string DiagnosticText => FormattableString.Invariant(
        $"冲突分组规模：资源匹配估算={ResourceMatchCount}；合并副本后估算={CollapsedResourceMatchCount}；上限={ResourceMatchLimit}；多余副本={RedundantCopyCount}；可用内存字节={AvailableMemoryBytes}；份额=1/{MemoryShareDivisor}；系数字节每匹配={BytesPerResourceMatch}；上限来源={(IsLimitOverridden ? "注入" : "内存预算")}");
}

public sealed class ConflictGroupingTooLargeException(ConflictGroupingSize size) : Exception(size.WarningMessage)
{
    public ConflictGroupingSize Size { get; } = size;
}
