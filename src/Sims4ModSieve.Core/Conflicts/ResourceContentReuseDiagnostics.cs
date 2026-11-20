namespace Sims4ModSieve.Core.Conflicts;

public enum ResourceContentReuseMode { Off, Auto, On }

/// <summary>逻辑 payload 输入量；不包含 header/索引，不代表物理磁盘 IO。</summary>
public sealed record ResourceContentReuseDiagnostics
{
    public ResourceContentReuseMode Requested { get; init; }
    public string? BypassReason { get; init; }
    public long NeedsDecompressionResources { get; init; }
    public long DeclaredOutputBytes { get; init; }
    public long StoredBytes { get; init; }
    public long SingletonMetadataResources { get; init; }
    public long FingerprintedResources { get; init; }
    public long FingerprintPreReadBytes { get; init; }
    public long DigestCandidates { get; init; }
    public long ConfirmedComparisons { get; init; }
    public long ConfirmationReadBytes { get; init; }
    public long TargetResources { get; init; }
    public long MetadataGroups { get; init; }
    public long EstimatedConfirmationBytes { get; init; }
    public long OriginalTargetsRead { get; init; }
    public long RepeatedOriginalTargets { get; init; }
    public long ConfirmationRejectedLoss { get; init; }
    public long ReaderFallbackLoss { get; init; }
    public long PublicationVersionLoss { get; init; }
    public long DigestPotentialSavings { get; init; }
    public long DisqualifiedBeforeConfirmationLoss { get; init; }
    public long ConfirmationByteLimitLoss { get; init; }
    public long CollisionSetLimitLoss { get; init; }
    public long ByteMismatchLoss { get; init; }
    public long ConfirmationReadFailureLoss { get; init; }
    public long PostConfirmationLoss { get; init; }
    public long DisqualifiedResources { get; init; }
    public long FingerprintUnavailableResources { get; init; }
    public long ConfirmationHandleOpens { get; init; }
    public long ConfirmationHandleHits { get; init; }
    public long ConfirmationHandleEvictions { get; init; }
    public long ConfirmationStampChecks { get; init; }
    public double ConfirmationMilliseconds { get; init; }
    public long ConfirmationByteLimit { get; init; }
    public long AccountedSavings => SavedDecompressions + DisqualifiedBeforeConfirmationLoss + ConfirmationByteLimitLoss
        + CollisionSetLimitLoss + ByteMismatchLoss + ConfirmationReadFailureLoss + PostConfirmationLoss;
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
    public string DiagnosticText => $"资源原始字节复用：请求={Requested}；旁路原因={BypassReason ?? "无"}；需解压={NeedsDecompressionResources}；声明输出字节={DeclaredOutputBytes}；合格zlib存储字节={StoredBytes}；单成员桶={SingletonMetadataResources}；指纹条目={FingerprintedResources}；指纹读取字节={FingerprintPreReadBytes}；摘要候选={DigestCandidates}；逐字节确认={ConfirmedComparisons}；确认读取字节={ConfirmationReadBytes}；请求目标={TargetResources}；元数据桶={MetadataGroups}；无碰撞确认估算={EstimatedConfirmationBytes}；原路径目标读取={OriginalTargetsRead}；原路径重复目标={RepeatedOriginalTargets}；确认失稳或IO撤销={ConfirmationRejectedLoss}；原reader回退损失={ReaderFallbackLoss}；发布失稳损失={PublicationVersionLoss}；摘要潜在节省={DigestPotentialSavings}；确认前失格损失={DisqualifiedBeforeConfirmationLoss}；确认字节上限损失={ConfirmationByteLimitLoss}；碰撞集合上限损失={CollisionSetLimitLoss}；字节不同损失={ByteMismatchLoss}；确认读取失败损失={ConfirmationReadFailureLoss}；确认后损失={PostConfirmationLoss}；失格条目={DisqualifiedResources}；无指纹条目={FingerprintUnavailableResources}；确认打开={ConfirmationHandleOpens}；句柄命中={ConfirmationHandleHits}；句柄淘汰={ConfirmationHandleEvictions}；确认取戳={ConfirmationStampChecks}；确认毫秒={ConfirmationMilliseconds:F3}；确认字节上限={ConfirmationByteLimit}；对账合计={AccountedSavings}；代表={Representatives}；等价集合={EquivalentSets}；原始字节相同跳过={SavedDecompressions}；避免输出字节={SavedOutputBytes}；额外读取毛额={GrossAdditionalReadBytes}；额外读取净额={NetAdditionalReadBytes}；回退={FallbackResources}；撤销={RevokedResources}；解压尝试={DecompressionAttempts}；解压成功={DecompressionCompleted}；实际输入字节={ActualInputBytes}；实际输出字节={ActualOutputBytes}；按大小跳过={SizeSkipped}";
}
