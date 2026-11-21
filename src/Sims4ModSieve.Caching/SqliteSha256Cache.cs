using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Caching;

public sealed partial class SqliteSha256Cache
{
    private const int SchemaVersion = 2;
    private const int PathKeyVersion = 2;
    // A normalized absolute path cannot start with this marker.
    private const char ExactPathMarker = ':';
    private const string TableDefinition = """
        (generation TEXT NOT NULL, path_key TEXT COLLATE ORDINAL_IGNORE_CASE NOT NULL, path TEXT NOT NULL,
         length INTEGER NOT NULL, write_ticks INTEGER NOT NULL, identity TEXT NOT NULL,
         sha256 BLOB NOT NULL CHECK(length(sha256) = 32),
         checksum BLOB NOT NULL CHECK(length(checksum) = 32),
         PRIMARY KEY(generation, path_key, length, write_ticks, identity)) STRICT
        """;
    private readonly string _connectionString;
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private volatile bool _available;

    public SqliteSha256Cache(string databasePath, string algorithmVersion = "SHA-256/v1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        var fullPath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Pooling = false, DefaultTimeout = 1
        }.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                return;
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS metadata
                (id INTEGER PRIMARY KEY CHECK(id = 1), generation TEXT NOT NULL,
                 algorithm_version TEXT NOT NULL,
                 schema_version INTEGER NOT NULL, path_key_version INTEGER NOT NULL) STRICT;
                """ + "CREATE TABLE IF NOT EXISTS hashes " + TableDefinition + ";"
                    + "CREATE TABLE IF NOT EXISTS resource_hashes " + ResourceTableDefinition + ";";
            command.ExecuteNonQuery();
            // Remove the obsolete version while keeping compatible hashes in the same generation.
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('metadata') WHERE name = 'application_version');";
            if (Equals(command.ExecuteScalar(), 1L))
            {
                command.CommandText = "ALTER TABLE metadata DROP COLUMN application_version;";
                command.ExecuteNonQuery();
            }
            command.CommandText = """
                SELECT generation FROM metadata WHERE id = 1 AND algorithm_version = $algorithm
                AND schema_version = $schema AND path_key_version = $path;
                """;
            command.Parameters.AddWithValue("$algorithm", algorithmVersion);
            command.Parameters.AddWithValue("$schema", SchemaVersion);
            command.Parameters.AddWithValue("$path", PathKeyVersion);
            if (command.ExecuteScalar() is string generation)
                _generation = generation;
            else
            {
                command.CommandText = """
                    DELETE FROM hashes;
                    DELETE FROM resource_hashes;
                    INSERT OR REPLACE INTO metadata (id, generation, algorithm_version, schema_version, path_key_version)
                    VALUES(1, $generation, $algorithm, $schema, $path);
                    """;
                command.Parameters.AddWithValue("$generation", _generation);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _available = true;
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { _available = false; }
    }

    public bool IsAvailable => _available;

    public string? GetSha256(string path, FileStamp stamp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = FileKey.Create(path, stamp);
        if (!_available) return null;
        try
        {
            using var connection = OpenConnection();
            return ReadSha256(connection, key, cancellationToken);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _available = false;
            return null;
        }
    }

    private string? ReadSha256(SqliteConnection connection, FileKey key, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN length(path) <= 32768 THEN path END,
                   CASE WHEN typeof(sha256) = 'blob' AND length(sha256) = 32 THEN sha256 END,
                   CASE WHEN typeof(checksum) = 'blob' AND length(checksum) = 32 THEN checksum END
            FROM main.hashes WHERE generation = $generation AND path_key = $key AND length = $length
            AND write_ticks = $ticks AND identity = $identity
            AND EXISTS(SELECT 1 FROM main.metadata WHERE id = 1 AND generation = $generation);
            """;
        BindKey(command, key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)
            || !key.PathComparer.Equals(reader.GetString(0), key.Path))
        {
            _available = false;
            return null;
        }
        var hash = (byte[])reader.GetValue(1);
        var storedKey = key with { Path = reader.GetString(0) };
        if (!CryptographicOperations.FixedTimeEquals(storedKey.Checksum(hash), (byte[])reader.GetValue(2)))
        {
            _available = false;
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash);
    }

    public Scan BeginScan()
    {
        SqliteConnection? connection = null;
        if (_available)
        {
            try
            {
                connection = OpenConnection();
                using var command = connection.CreateCommand();
                // TEMP belongs to this connection and disappears even if the process is killed.
                command.CommandText = "PRAGMA temp_store=FILE; CREATE TEMP TABLE staged " + TableDefinition + ";"
                    + "CREATE TEMP TABLE staged_resources " + ResourceTableDefinition + ";";
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _available = false;
                connection?.Dispose();
                connection = null;
            }
        }
        return new Scan(this, connection);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // The v1 schema names this collation, and its unmarked keys keep their original order.
            connection.CreateCollation("ORDINAL_IGNORE_CASE", ComparePathKeys);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static int ComparePathKeys(string left, string right)
    {
        var leftIsExact = left.StartsWith(ExactPathMarker);
        var rightIsExact = right.StartsWith(ExactPathMarker);
        if (leftIsExact != rightIsExact) return leftIsExact ? -1 : 1;
        return GetPathComparer(leftIsExact).Compare(left, right);
    }

    private static StringComparer GetPathComparer(bool exact) =>
        exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private void BindKey(SqliteCommand command, FileKey key)
    {
        command.Parameters.AddWithValue("$generation", _generation);
        command.Parameters.AddWithValue("$key", key.IndexedPath);
        command.Parameters.AddWithValue("$length", key.Length);
        command.Parameters.AddWithValue("$ticks", key.Ticks);
        command.Parameters.AddWithValue("$identity", key.Identity);
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException;

    private sealed record FileKey(string Path, long Length, long Ticks, string Identity)
    {
        private bool RequiresExactPath => Identity.Length == 0;
        public string IndexedPath => RequiresExactPath ? ExactPathMarker + Path : Path;
        public StringComparer PathComparer => GetPathComparer(RequiresExactPath);

        public static FileKey Create(string path, FileStamp stamp)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(stamp.Length);
            if (stamp.LastWriteTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("File stamp time must be UTC.", nameof(stamp));
            var normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (normalized.Length > 32768) throw new ArgumentException("Path is too long.", nameof(path));
            var identity = stamp.Identity is { } id
                ? $"{id.VolumeSerialNumber:X16}{id.FileIdLow:X16}{id.FileIdHigh:X16}" : "";
            return new(normalized, stamp.Length, stamp.LastWriteTimeUtc.Ticks, identity);
        }

        public byte[] Checksum(byte[] hash, string resource = "") => SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture,
                $"{Path}\n{Length}\n{Ticks}\n{Identity}\n{Convert.ToHexString(hash)}{resource}")));
    }

    public sealed partial class Scan : IDisposable
    {
        private readonly SqliteSha256Cache _owner;
        private readonly object _gate = new();
        private SqliteConnection? _connection;
        private bool _hasStagedRows;

        internal Scan(SqliteSha256Cache owner, SqliteConnection? connection)
        {
            _owner = owner;
            _connection = connection;
        }

        public string? GetSha256(string path, FileStamp stamp, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FileKey.Create(path, stamp);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_owner._available || _connection is null) return null;
                try
                {
                    return _owner.ReadSha256(_connection, key, cancellationToken);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _owner._available = false;
                    Dispose();
                    return null;
                }
            }
        }

        public bool Stage(string path, FileStamp stamp, string sha256, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FileKey.Create(path, stamp);
            if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
                throw new ArgumentException("Expected a complete SHA-256 hex digest.", nameof(sha256));
            var hash = Convert.FromHexString(sha256);
            lock (_gate)
            {
                if (!_owner._available || _connection is null) return false;
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = """
                        DELETE FROM temp.staged WHERE generation = $generation AND path_key IN ($key, $alternate)
                        AND (path_key = $key OR path = $path);
                        INSERT OR REPLACE INTO temp.staged
                        VALUES($generation, $key, $path, $length, $ticks, $identity, $sha, $checksum);
                        """;
                    _owner.BindKey(command, key);
                    command.Parameters.AddWithValue("$alternate", key.Identity.Length == 0 ? key.Path : ExactPathMarker + key.Path);
                    command.Parameters.AddWithValue("$path", key.Path);
                    command.Parameters.AddWithValue("$sha", hash);
                    command.Parameters.AddWithValue("$checksum", key.Checksum(hash));
                    command.ExecuteNonQuery();
                    _hasStagedRows = true;
                    return true;
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _owner._available = false;
                    Dispose();
                    return false;
                }
            }
        }

        public bool Commit(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_owner._available || _connection is null) return false;
                    if (!_hasStagedRows) return true;
                    using var transaction = _connection.BeginTransaction();
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "SELECT generation FROM metadata WHERE id = 1;";
                    if (!Equals(command.ExecuteScalar(), _owner._generation))
                    {
                        _owner._available = false;
                        return false;
                    }
                    // Across identity modes, only an exact path match can retire an older version.
                    command.CommandText = """
                        DELETE FROM main.hashes WHERE generation = $generation
                        AND path_key IN (SELECT path_key FROM temp.staged);
                        DELETE FROM main.hashes WHERE generation = $generation
                        AND (path_key, path) IN (SELECT CASE WHEN identity = '' THEN path ELSE ':' || path END, path FROM temp.staged);
                        INSERT INTO main.hashes SELECT * FROM temp.staged;
                        """ + PublishResourceHashes;
                    command.Parameters.AddWithValue("$generation", _owner._generation);
                    command.ExecuteNonQuery();
                    // Cancellation after this check may race with the complete atomic publication.
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                    return true;
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _owner._available = false;
                    return false;
                }
                finally { Dispose(); }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }
    }
}
