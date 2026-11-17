using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sims4ModSieve.Benchmarks;

internal static partial class ResultSnapshot
{
    internal static async Task WriteAsync(string path, object value, CancellationToken cancellationToken)
    {
        var temporary = path + ".partial";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, ResultSnapshot.JsonOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Validate(string path) => Fingerprint(path);

    internal static string? FirstDifference(string expected, string actual)
    {
        if (expected == actual || Fingerprint(expected).AsSpan().SequenceEqual(Fingerprint(actual))) return null;
        using var left = new Reader(expected);
        using var right = new Reader(actual);
        left.Read();
        right.Read();
        return Compare(left, right, "$");
    }

    private static byte[] Fingerprint(string path)
    {
        using var reader = new Reader(path);
        if (!reader.Read() || reader.Kind != JsonTokenType.StartObject)
            throw new JsonException("Baseline snapshot must be an object.");
        var digest = HashValue(reader);
        if (reader.Read()) throw new JsonException("Unexpected trailing snapshot content.");
        return digest;
    }

    // Only the current object's property names/digests are retained; arrays are streamed in order.
    private static byte[] HashValue(Reader reader)
    {
        if (reader.Kind == JsonTokenType.StartObject)
        {
            var properties = Properties(reader);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("object"u8);
            Span<byte> length = stackalloc byte[sizeof(int)];
            foreach (var (name, value) in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
                hash.AppendData(value.Digest);
            }
            return hash.GetHashAndReset();
        }
        if (reader.Kind == JsonTokenType.StartArray)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("array"u8);
            while (reader.Read() && reader.Kind != JsonTokenType.EndArray)
                hash.AppendData(HashValue(reader));
            if (reader.Kind != JsonTokenType.EndArray) throw new JsonException("Unterminated snapshot array.");
            return hash.GetHashAndReset();
        }
        var text = reader.Text;
        if (reader.Kind == JsonTokenType.Number)
            text = NormalizeNumber(text!);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{reader.Kind}:{text}"));
    }

    private static string NormalizeNumber(string text)
    {
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero
            : BigInteger.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        if (point >= 0) exponent -= mantissa.Length - point - 1;
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length;
        return (text[0] == '-' ? "-" : "") + significant + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, (long Offset, byte[] Digest)> Properties(Reader reader)
    {
        var properties = new Dictionary<string, (long, byte[])>(StringComparer.Ordinal);
        while (reader.Read() && reader.Kind != JsonTokenType.EndObject)
        {
            if (reader.Kind != JsonTokenType.PropertyName) throw new JsonException("Expected snapshot property.");
            var name = reader.Text!;
            if (!reader.Read()) throw new JsonException("Missing snapshot property value.");
            var offset = reader.Offset;
            if (!properties.TryAdd(name, (offset, HashValue(reader))))
                throw new JsonException("Duplicate snapshot property.");
        }
        if (reader.Kind != JsonTokenType.EndObject) throw new JsonException("Unterminated snapshot object.");
        return properties;
    }

    private static string? Compare(Reader left, Reader right, string path)
    {
        if (left.Kind != right.Kind) return $"{path}: value differs";
        if (left.Kind == JsonTokenType.StartObject)
        {
            var a = Properties(left);
            var b = Properties(right);
            foreach (var key in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
            {
                if (!a.TryGetValue(key, out var first) || !b.TryGetValue(key, out var second))
                    return $"{path}.{key}: missing property";
                if (first.Digest.AsSpan().SequenceEqual(second.Digest)) continue;
                left.Seek(first.Offset);
                right.Seek(second.Offset);
                return Compare(left, right, $"{path}.{key}");
            }
            return null;
        }
        if (left.Kind == JsonTokenType.StartArray)
        {
            var a = 0;
            var b = 0;
            (long Left, long Right, int Index)? different = null;
            while (true)
            {
                var hasLeft = left.Read() && left.Kind != JsonTokenType.EndArray;
                var hasRight = right.Read() && right.Kind != JsonTokenType.EndArray;
                if (!hasLeft && !hasRight) break;
                var leftOffset = left.Offset;
                var rightOffset = right.Offset;
                var first = hasLeft ? HashValue(left) : null;
                var second = hasRight ? HashValue(right) : null;
                if (hasLeft && hasRight && !first.AsSpan().SequenceEqual(second) && different is null)
                    different = (leftOffset, rightOffset, a);
                if (hasLeft) a++;
                if (hasRight) b++;
                // An ended reader must stay at EndArray while the other is still being counted.
                if (!hasLeft || !hasRight)
                {
                    var remaining = hasLeft ? left : right;
                    while (remaining.Read() && remaining.Kind != JsonTokenType.EndArray)
                    {
                        HashValue(remaining);
                        if (hasLeft) a++; else b++;
                    }
                    break;
                }
            }
            if (a != b) return $"{path}: count {a} != {b}";
            if (different is not { } item) return null;
            left.Seek(item.Left);
            right.Seek(item.Right);
            return Compare(left, right, $"{path}[{item.Index}]");
        }
        return HashValue(left).AsSpan().SequenceEqual(HashValue(right)) ? null : $"{path}: value differs";
    }

    private sealed class Reader(string path) : IDisposable
    {
        private readonly FileStream stream = File.OpenRead(path);
        private byte[] buffer = new byte[64 * 1024];
        private int start;
        private int length;
        private long bufferOffset;
        private bool final;
        private JsonReaderState state;

        internal JsonTokenType Kind { get; private set; }
        internal string? Text { get; private set; }
        internal long Offset { get; private set; }

        internal bool Read()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(start, length - start), final, state);
                if (reader.Read())
                {
                    Offset = bufferOffset + start + reader.TokenStartIndex;
                    Kind = reader.TokenType;
                    Text = Kind is JsonTokenType.String or JsonTokenType.PropertyName ? reader.GetString()
                        : Kind == JsonTokenType.Number ? Encoding.UTF8.GetString(reader.ValueSpan) : null;
                    start += (int)reader.BytesConsumed;
                    state = reader.CurrentState;
                    return true;
                }
                start += (int)reader.BytesConsumed;
                state = reader.CurrentState;
                if (final) return false;
                bufferOffset += start;
                buffer.AsSpan(start, length - start).CopyTo(buffer);
                length -= start;
                start = 0;
                if (length == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
                var read = stream.Read(buffer.AsSpan(length));
                length += read;
                final = read == 0;
            }
        }

        internal void Seek(long offset)
        {
            if (offset >= bufferOffset && offset < bufferOffset + length)
                start = (int)(offset - bufferOffset);
            else
            {
                stream.Seek(offset, SeekOrigin.Begin);
                bufferOffset = offset;
                start = length = 0;
                final = false;
            }
            state = default;
            if (!Read()) throw new JsonException("Missing snapshot value.");
        }

        public void Dispose() => stream.Dispose();
    }
}
