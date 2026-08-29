using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Finality.Persistence;

/// <summary>Opens finality SQLite connections and applies its owned migrations.</summary>
public sealed class FinalityDatabase
{
    private readonly FinalityDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;

    public FinalityDatabase(FinalityDatabaseOptions options, TimeProvider timeProvider)
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_options.DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The finality database path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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
            connection,
            transaction,
            cancellationToken);
        long latestKnownVersion = FinalityDatabaseMigrations.All[^1].Version;
        long unsupportedVersion = applied.Keys.FirstOrDefault(version => version > latestKnownVersion);
        if (unsupportedVersion > 0)
        {
            throw new InvalidOperationException(
                $"Database schema version {unsupportedVersion} is newer than supported version {latestKnownVersion}.");
        }

        foreach (FinalityDatabaseMigration expected in FinalityDatabaseMigrations.All)
        {
            if (applied.TryGetValue(expected.Version, out string? name) &&
                !string.Equals(name, expected.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database migration {expected.Version} is named '{name}', expected '{expected.Name}'.");
            }
        }

        foreach (FinalityDatabaseMigration migration in FinalityDatabaseMigrations.All)
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
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

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
