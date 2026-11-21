using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Sims4ModSieve.Core.Conflicts;

namespace Sims4ModSieve.Caching;

public sealed partial class SqliteSha256Cache
{
    private const string ResourceTableDefinition = """
        (generation TEXT NOT NULL, path_key TEXT COLLATE ORDINAL_IGNORE_CASE NOT NULL, path TEXT NOT NULL,
         length INTEGER NOT NULL, write_ticks INTEGER NOT NULL, identity TEXT NOT NULL,
         target TEXT NOT NULL, limits TEXT NOT NULL, output_length INTEGER NOT NULL CHECK(output_length >= 0),
         sha256 BLOB NOT NULL CHECK(length(sha256) = 32),
         checksum BLOB NOT NULL CHECK(length(checksum) = 32),
         PRIMARY KEY(generation, path_key, length, write_ticks, identity, target, limits)) STRICT
        """;

    private const string PublishResourceHashes = """
        DELETE FROM main.resource_hashes AS old WHERE generation = $generation
        AND path_key IN (SELECT path_key FROM temp.staged_resources)
        AND NOT EXISTS (SELECT 1 FROM temp.staged_resources AS staged
            WHERE staged.generation = old.generation AND staged.path_key = old.path_key
            AND staged.length = old.length AND staged.write_ticks = old.write_ticks AND staged.identity = old.identity);
        DELETE FROM main.resource_hashes WHERE generation = $generation AND (path_key, path) IN
            (SELECT CASE WHEN identity = '' THEN path ELSE ':' || path END, path FROM temp.staged_resources);
        INSERT OR REPLACE INTO main.resource_hashes SELECT * FROM temp.staged_resources;
        """;

    private static string EncodeTarget(ResourceContentTarget target) =>
        string.Create(CultureInfo.InvariantCulture, $"{target.Key.Type:X8}{target.Key.Group:X8}{target.Key.Instance:X16}{target.Ordinal:X8}");

    private static string EncodeLimits(ResourceContentLimits limits) => string.Create(CultureInfo.InvariantCulture,
        $"{limits.MaxStoredBytesPerResource:X16}{limits.MaxDecompressedBytesPerResource:X16}");

    private static byte[] ResourceChecksum(FileKey key, byte[] hash, string target, string limits, long outputLength) =>
        key.Checksum(hash, string.Create(CultureInfo.InvariantCulture, $"\n{target}\n{limits}\n{outputLength}"));

    public sealed partial class Scan
    {
        internal ResourceContentResult?[]? GetResourceHashes(ResourceContentBatchRequest request, CancellationToken token)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_owner._available || _connection is null) return null;
                var key = FileKey.Create(request.PackagePath, request.ExpectedStamp);
                var limits = EncodeLimits(request.Limits);
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = """
                        SELECT CASE WHEN length(path) <= 32768 THEN path END, output_length,
                               CASE WHEN typeof(sha256) = 'blob' AND length(sha256) = 32 THEN sha256 END,
                               CASE WHEN typeof(checksum) = 'blob' AND length(checksum) = 32 THEN checksum END
                        FROM main.resource_hashes WHERE generation = $generation AND path_key = $key AND length = $length
                        AND write_ticks = $ticks AND identity = $identity AND target = $target AND limits = $limits
                        AND EXISTS(SELECT 1 FROM main.metadata WHERE id = 1 AND generation = $generation);
                        """;
                    _owner.BindKey(command, key);
                    command.Parameters.AddWithValue("$limits", limits);
                    var targetParameter = command.Parameters.AddWithValue("$target", "");
                    var results = new ResourceContentResult?[request.Targets.Count];
                    for (var i = 0; i < results.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var target = request.Targets[i];
                        var encoded = EncodeTarget(target);
                        targetParameter.Value = encoded;
                        using var reader = command.ExecuteReader();
                        if (!reader.Read()) continue;
                        if (reader.IsDBNull(0) || reader.IsDBNull(2) || reader.IsDBNull(3)
                            || !key.PathComparer.Equals(reader.GetString(0), key.Path))
                        {
                            _owner._available = false;
                            return null;
                        }
                        var hash = (byte[])reader.GetValue(2);
                        var outputLength = reader.GetInt64(1);
                        var checksum = ResourceChecksum(key with { Path = reader.GetString(0) }, hash, encoded, limits, outputLength);
                        if (!CryptographicOperations.FixedTimeEquals(checksum, (byte[])reader.GetValue(3)))
                        {
                            _owner._available = false;
                            return null;
                        }
                        results[i] = ResourceContentResult.Success(target, Convert.ToHexString(hash), outputLength);
                    }
                    return results;
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _owner._available = false;
                    Dispose();
                    return null;
                }
            }
        }

        internal void StageResourceHashes(ResourceContentBatchRequest request,
            IReadOnlyList<ResourceContentResult> results, CancellationToken token)
        {
            if (!results.Any(result => result.IsSuccess)) return;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_owner._available || _connection is null) return;
                var key = FileKey.Create(request.PackagePath, request.ExpectedStamp);
                var limits = EncodeLimits(request.Limits);
                try
                {
                    // A transaction groups this package's TEMP writes without locking the main database for writing.
                    using var transaction = _connection.BeginTransaction(deferred: true);
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    _owner.BindKey(command, key);
                    command.Parameters.AddWithValue("$path", key.Path);
                    var alternate = command.Parameters.AddWithValue("$alternate", key.Identity.Length == 0 ? key.Path : ExactPathMarker + key.Path);
                    command.CommandText = """
                        DELETE FROM temp.staged_resources WHERE generation = $generation AND path_key IN ($key, $alternate) AND
                        ((path_key = $key AND (length, write_ticks, identity) <> ($length, $ticks, $identity))
                         OR (path_key = $alternate AND path = $path));
                        """;
                    command.ExecuteNonQuery();
                    command.Parameters.Remove(alternate);

                    command.CommandText = """
                        INSERT OR REPLACE INTO temp.staged_resources VALUES
                        ($generation, $key, $path, $length, $ticks, $identity, $target, $limits, $output, $sha, $checksum);
                        """;
                    command.Parameters.AddWithValue("$limits", limits);
                    var targetParameter = command.Parameters.AddWithValue("$target", "");
                    var outputParameter = command.Parameters.AddWithValue("$output", 0L);
                    var hashParameter = command.Parameters.Add("$sha", SqliteType.Blob);
                    var checksumParameter = command.Parameters.Add("$checksum", SqliteType.Blob);
                    foreach (var result in results)
                    {
                        token.ThrowIfCancellationRequested();
                        if (result.Sha256 is not { } sha256) continue;
                        var hash = Convert.FromHexString(sha256);
                        var target = EncodeTarget(result.Target);
                        var outputLength = result.DecompressedLength!.Value;
                        targetParameter.Value = target;
                        outputParameter.Value = outputLength;
                        hashParameter.Value = hash;
                        checksumParameter.Value = ResourceChecksum(key, hash, target, limits, outputLength);
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    _hasStagedRows = true;
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _owner._available = false;
                    Dispose();
                }
            }
        }
    }
}
