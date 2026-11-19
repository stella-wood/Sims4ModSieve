using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Benchmarks;

internal readonly record struct RawResourceDescriptor(
    ResourceKey Key, uint Position, uint StoredSize, uint DeclaredSize, ushort Compression)
{
    internal bool NeedsDecompression(long fileLength) => Compression == 0x5A42
        && StoredSize <= ResourceContentLimits.DefaultMaxStoredBytesPerResource
        && DeclaredSize <= ResourceContentLimits.DefaultMaxDecompressedBytesPerResource
        && Position >= DbpfPrecheck.HeaderLength && (ulong)Position + StoredSize <= (ulong)fileLength;
}

// 独立的只读定位器；不向生产读取器暴露 offset，也不改变其校验与内容读取路径。
internal static class RawResourceIndex
{
    internal static Dictionary<int, RawResourceDescriptor> Read(
        Stream stream, long fileLength, IReadOnlyList<ResourceContentTarget> targets)
    {
        var header = new byte[DbpfPrecheck.HeaderLength];
        stream.ReadExactly(header);
        var issue = DbpfPrecheck.ValidateHeader(header, fileLength, string.Empty, PackageSafetyLimits.Default, out var parsed);
        if (issue is not null) throw new InvalidDataException(issue.Code);
        var entries = new Dictionary<int, RawResourceDescriptor>();
        if (targets.Count == 0) return entries;
        var wanted = targets.ToDictionary(target => target.Ordinal, target => target.Key);
        if (wanted.Keys.Any(ordinal => ordinal < 0 || ordinal >= parsed.EntryCount))
            throw new InvalidDataException("资源 ordinal 超出索引范围");
        stream.Position = (long)parsed.IndexPosition;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        long remaining = parsed.IndexSize;
        uint Read()
        {
            if (remaining < sizeof(uint)) throw new EndOfStreamException("索引截断");
            remaining -= sizeof(uint);
            return reader.ReadUInt32();
        }
        var flags = Read();
        issue = DbpfPrecheck.ValidateIndexShape(flags, parsed.EntryCount, parsed.IndexSize, string.Empty);
        if (issue is not null) throw new InvalidDataException(issue.Code);
        uint? type = (flags & 1) != 0 ? Read() : null;
        uint? group = (flags & 2) != 0 ? Read() : null;
        uint? instanceHi = (flags & 4) != 0 ? Read() : null;
        var last = wanted.Keys.Max();
        for (var ordinal = 0; ordinal <= last; ordinal++)
        {
            var t = type ?? Read();
            var g = group ?? Read();
            var hi = instanceHi ?? Read();
            var lo = Read();
            var position = Read();
            var size = Read();
            var declared = Read();
            var compression = (size & 0x8000_0000) != 0 ? (ushort)Read() : (ushort)0;
            if (!wanted.TryGetValue(ordinal, out var expected)) continue;
            var key = new ResourceKey(t, g, ((ulong)hi << 32) | lo);
            if (key != expected) throw new InvalidDataException("资源 TGI 与候选不符");
            entries.Add(ordinal, new(key, position, size & 0x7FFF_FFFF, declared, compression));
        }
        return entries;
    }
}
