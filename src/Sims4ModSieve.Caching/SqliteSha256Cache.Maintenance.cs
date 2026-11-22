using Microsoft.Data.Sqlite;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Caching;

public sealed partial class SqliteSha256Cache
{
    public static string DatabasePath(string dataDirectory) => Path.Combine(dataDirectory, "cache", "sha256.sqlite3");

    public static long? GetSize(string dataDirectory)
    {
        var database = DatabasePath(dataDirectory);
        long bytes = 0;
        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            try { bytes += new FileInfo(path).Length; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (IsStorageFailure(ex)) { return null; }
        }
        return bytes;
    }

    public static bool Clear(string dataDirectory)
    {
        if (GetSize(dataDirectory) == 0) return true;
        var cache = new SqliteSha256Cache(DatabasePath(dataDirectory));
        if (!cache.IsAvailable) return false;
        try
        {
            using var connection = cache.OpenConnection();
            using var command = connection.CreateCommand();
            using var transaction = connection.BeginTransaction();
            var generation = Guid.NewGuid().ToString("N");
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM hashes;
                DELETE FROM resource_hashes;
                UPDATE metadata SET generation = $generation, deleted_count = 0 WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$generation", generation);
            command.ExecuteNonQuery();
            transaction.Commit();
            return TryCompact(connection, generation, 0);
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { return false; }
    }

    private static int PruneMissing(SqliteConnection connection, SqliteCommand command, ScanCoverage coverage)
    {
        var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
        connection.CreateFunction<string, bool>("scan_missing", path =>
        {
            if (!decisions.TryGetValue(path, out var missing))
                decisions.Add(path, missing = coverage.ProvesMissing(path));
            return missing;
        }, isDeterministic: true);
        command.CommandText = """
            DELETE FROM main.hashes WHERE scan_missing(path);
            DELETE FROM main.resource_hashes WHERE scan_missing(path);
            """;
        return command.ExecuteNonQuery();
    }

    private static long CountForCompaction(SqliteCommand command, int removed)
    {
        command.CommandText = """
            UPDATE metadata SET deleted_count = deleted_count + $removed WHERE id = 1;
            SELECT deleted_count FROM metadata WHERE id = 1
              AND 3 * deleted_count > (SELECT count(*) FROM hashes) + (SELECT count(*) FROM resource_hashes);
            """;
        command.Parameters.AddWithValue("$removed", removed);
        return command.ExecuteScalar() is long count ? count : 0;
    }

    private static bool TryCompact(SqliteConnection connection, string generation, long deletedCount)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            if (deletedCount > 0)
            {
                // Keep the count if another scan removed more rows during maintenance.
                command.CommandText = """
                    UPDATE metadata SET deleted_count = 0
                    WHERE id = 1 AND generation = $generation AND deleted_count = $count;
                    """;
                command.Parameters.AddWithValue("$generation", generation);
                command.Parameters.AddWithValue("$count", deletedCount);
                command.ExecuteNonQuery();
            }
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            return Equals(command.ExecuteScalar(), 0L);
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { return false; }
    }
}
