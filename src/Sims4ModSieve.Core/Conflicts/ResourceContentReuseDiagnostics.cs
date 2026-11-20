namespace Sims4ModSieve.Core.Conflicts;

public enum ResourceContentReuseMode { Off, Auto, On }

/// <summary>逻辑 payload 输入量；不包含 header/索引，不代表物理磁盘 IO。</summary>
public sealed record ResourceContentReuseDiagnostics
{
    public ResourceContentReuseMode Requested { get; init; }
    public string? BypassReason { get; init; }
    public long NeedsDecompressionResources { get; init; }
    public long DeclaredOutputBytes { get; init; }
    public long SingletonMetadataResources { get; init; }
    public long FingerprintedResources { get; init; }
    public long FingerprintPreReadBytes { get; init; }
    public long DigestCandidates { get; init; }
    public long ConfirmedComparisons { get; init; }
    public long ConfirmationReadBytes { get; init; }
    public long Representatives { get; init; }
    public long EquivalentSets { get; init; }
    public long SavedDecompressions { get; init; }
    public long SavedOutputBytes { get; init; }
    public long AvoidedOriginalReadBytes { get; init; }
    public long FallbackResources { get; init; }
    public long RevokedResources { get; init; }
    public long DecompressionAttempts { get; init; }
    public long DecompressionCompleted { get; init; }
    public long ActualInputBytes { get; init; }
    public long ActualOutputBytes { get; init; }
    public long GrossAdditionalReadBytes => FingerprintPreReadBytes + ConfirmationReadBytes;
    public long NetAdditionalReadBytes => GrossAdditionalReadBytes - AvoidedOriginalReadBytes;
    public long SizeSkipped => 0;
    public string DiagnosticText => $"资源原始字节复用：请求={Requested}；旁路原因={BypassReason ?? "无"}；需解压={NeedsDecompressionResources}；声明输出字节={DeclaredOutputBytes}；单成员桶={SingletonMetadataResources}；指纹条目={FingerprintedResources}；指纹读取字节={FingerprintPreReadBytes}；摘要候选={DigestCandidates}；逐字节确认={ConfirmedComparisons}；确认读取字节={ConfirmationReadBytes}；代表={Representatives}；等价集合={EquivalentSets}；原始字节相同跳过={SavedDecompressions}；避免输出字节={SavedOutputBytes}；额外读取毛额={GrossAdditionalReadBytes}；额外读取净额={NetAdditionalReadBytes}；回退={FallbackResources}；撤销={RevokedResources}；解压尝试={DecompressionAttempts}；解压成功={DecompressionCompleted}；实际输入字节={ActualInputBytes}；实际输出字节={ActualOutputBytes}；按大小跳过={SizeSkipped}";
}
