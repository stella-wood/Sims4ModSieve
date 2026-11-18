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
    public static int Resolve(DuplicateHashConcurrency setting, IReadOnlyCollection<StorageVolumeProfile> volumes) =>
        setting switch
        {
            DuplicateHashConcurrency.One or DuplicateHashConcurrency.Two or DuplicateHashConcurrency.Four or DuplicateHashConcurrency.Eight
                => (int)setting,
            _ when volumes.Any(volume => volume.IncursSeekPenalty == true) => 1,
            _ when volumes.Count > 0 && volumes.All(volume => volume.IncursSeekPenalty == false) => 4,
            _ => 2,
        };
}
