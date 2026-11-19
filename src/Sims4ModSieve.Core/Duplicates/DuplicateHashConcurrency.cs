namespace Sims4ModSieve.Core.Duplicates;

public enum DuplicateHashConcurrency
{
    Auto = 0,
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8,
}

public readonly record struct StorageVolumeProfile(string? VolumeId, bool? IncursSeekPenalty, string? UnknownReason = null);

public interface IStorageProfile
{
    StorageVolumeProfile GetVolume(string directoryPath);
}

internal static class DuplicateHashConcurrencyPolicy
{
    /// <summary>整文件哈希在全 SSD 上的 Auto 上限（阶段 1 SSD 对比定的值）。</summary>
    public const int DuplicateHashSsdAutoLimit = 4;

    /// <summary>
    /// package 索引与资源内容在全 SSD 上的 Auto 上限。阶段 2 SSD 基准（1376 文件 / 9.65 GiB）中 8 比 4 总耗时短约 19%，
    /// 工作集只多约 6 MiB；内容阶段以解压和哈希为主，按逻辑 CPU 数封顶，避免少核机器过度订阅。
    /// </summary>
    public static int PackageSsdAutoLimit { get; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    public static int Resolve(DuplicateHashConcurrency setting, IReadOnlyCollection<StorageVolumeProfile> volumes,
        int ssdAutoLimit = DuplicateHashSsdAutoLimit) =>
        setting switch
        {
            DuplicateHashConcurrency.One or DuplicateHashConcurrency.Two or DuplicateHashConcurrency.Four or DuplicateHashConcurrency.Eight
                => (int)setting,
            _ when volumes.Any(volume => volume.IncursSeekPenalty == true) => 1,
            _ when volumes.Count > 0 && volumes.All(volume => volume.IncursSeekPenalty == false) => ssdAutoLimit,
            _ => 2,
        };
}
