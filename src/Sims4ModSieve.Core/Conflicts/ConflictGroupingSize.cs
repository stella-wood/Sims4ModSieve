namespace Sims4ModSieve.Core.Conflicts;

public sealed record ConflictGroupingSize(long ResourceMatchCount, long CollapsedResourceMatchCount,
    long ResourceMatchLimit, int RedundantCopyCount)
{
    // #44：1 GiB 托管堆下约 628 万匹配成功、2515 万失败。暂取 1200 万，
    // 为已成功规模留近一倍余量；最终值须用用户的正常 Mods + 备份扫描计数校准。
    public const long DefaultResourceMatchLimit = 12_000_000;
    public const string IssueCode = "conflict-grouping-too-large";

    public bool ExceedsLimit => ResourceMatchCount > ResourceMatchLimit;
    public string? Reason => !ExceedsLimit ? null : CollapsedResourceMatchCount <= ResourceMatchLimit
        ? "duplicate-copies" : "too-many-shared-resources";
    public string WarningMessage => Reason == "duplicate-copies"
        ? $"⚠️文件夹内存在大量相同副本（{RedundantCopyCount}份），建议优先进行重复检测"
        : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测";

    public string DiagnosticText => FormattableString.Invariant(
        $"冲突分组规模：资源匹配估算={ResourceMatchCount}；合并副本后估算={CollapsedResourceMatchCount}；上限={ResourceMatchLimit}；多余副本={RedundantCopyCount}");
}

public sealed class ConflictGroupingTooLargeException(ConflictGroupingSize size) : Exception(size.WarningMessage)
{
    public ConflictGroupingSize Size { get; } = size;
}
