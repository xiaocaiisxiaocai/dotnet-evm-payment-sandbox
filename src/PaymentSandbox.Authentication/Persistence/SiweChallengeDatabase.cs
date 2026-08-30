using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Authentication.Persistence;

/// <summary>Opens the dedicated SIWE SQLite file and applies its owned migrations.</summary>
public sealed class SiweChallengeDatabase
{
    private readonly SiweChallengeDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;

    public SiweChallengeDatabase(
        SiweChallengeDatabaseOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath => _options.DatabasePath;
    public int Capacity => _options.Capacity;
    public int SessionCapacity => _options.SessionCapacity;

    /// <summary>Creates the parent directory and atomically advances the schema.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_options.DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                "The SIWE challenge database path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(
            connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);

        // Immediate mode obtains SQLite's write reservation before reading the
        // migration table. Concurrent initializers therefore serialize before
        // either one decides which migration is missing.
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
                name TEXT NOT NULL UNIQUE,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """,
            cancellationToken);

        Dictionary<long, string> applied = await ReadAppliedMigrationsAsync(
            connection, transaction, cancellationToken);
        long latestKnownVersion = SiweChallengeDatabaseMigrations.All[^1].Version;
        long unsupportedVersion = applied.Keys.FirstOrDefault(
            version => version > latestKnownVersion);
        if (unsupportedVersion > 0)
        {
            throw new InvalidOperationException(
                $"Database schema version {unsupportedVersion} is newer than supported version {latestKnownVersion}.");
        }

        foreach (SiweChallengeDatabaseMigration expected in SiweChallengeDatabaseMigrations.All)
        {
            if (applied.TryGetValue(expected.Version, out string? name) &&
                !string.Equals(name, expected.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database migration {expected.Version} is named '{name}', expected '{expected.Name}'.");
            }
        }

        foreach (SiweChallengeDatabaseMigration migration in SiweChallengeDatabaseMigrations.All)
        {
            if (applied.ContainsKey(migration.Version))
            {
                continue;
            }

            await ExecuteNonQueryAsync(connection, transaction, migration.Sql, cancellationToken);
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES ($version, $name, $appliedAtUtc);
                """;
            insert.Parameters.AddWithValue("$version", migration.Version);
            insert.Parameters.AddWithValue("$name", migration.Name);
            insert.Parameters.AddWithValue(
                "$appliedAtUtc",
                _timeProvider.GetUtcNow().ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureCapacitiesAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                null,
                "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;",
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<Dictionary<long, string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";
        var migrations = new Dictionary<long, string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(reader.GetInt64(0), reader.GetString(1));
        }

        return migrations;
    }

    private async Task EnsureCapacitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO siwe_store_settings (singleton, capacity, session_capacity)
                VALUES (1, $capacity, $sessionCapacity)
                ON CONFLICT (singleton) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$capacity", _options.Capacity);
            insert.Parameters.AddWithValue("$sessionCapacity", _options.SessionCapacity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // A Week 16 database already has the singleton row. Migration 2 adds a
        // nullable column so this one reviewed initialization can fill it once.
        await using (SqliteCommand initializeSessionCapacity = connection.CreateCommand())
        {
            initializeSessionCapacity.Transaction = transaction;
            initializeSessionCapacity.CommandText =
                """
                UPDATE siwe_store_settings
                SET session_capacity = $sessionCapacity
                WHERE singleton = 1 AND session_capacity IS NULL;
                """;
            initializeSessionCapacity.Parameters.AddWithValue(
                "$sessionCapacity", _options.SessionCapacity);
            await initializeSessionCapacity.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT capacity, session_capacity FROM siwe_store_settings WHERE singleton = 1;";
        await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != _options.Capacity ||
            reader.GetInt64(1) != _options.SessionCapacity)
        {
            throw new InvalidOperationException(
                "The configured SIWE challenge or session capacity does not match the database-owned capacity.");
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
