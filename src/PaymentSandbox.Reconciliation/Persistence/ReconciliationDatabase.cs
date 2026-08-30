using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Reconciliation.Persistence;

/// <summary>Owns the independent reconciliation SQLite schema.</summary>
public sealed class ReconciliationDatabase
{
    private readonly ReconciliationDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;

    public ReconciliationDatabase(ReconciliationDatabaseOptions options, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory."));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ExecuteAsync(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
                name TEXT NOT NULL UNIQUE,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """, cancellationToken);
        var applied = new Dictionary<long, string>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt64(0), reader.GetString(1));
            }
        }

        long latest = ReconciliationDatabaseMigrations.All[^1].Version;
        if (applied.Keys.Any(version => version > latest))
        {
            throw new InvalidOperationException("The reconciliation database schema is newer than this application.");
        }

        foreach (ReconciliationDatabaseMigration migration in ReconciliationDatabaseMigrations.All)
        {
            if (applied.TryGetValue(migration.Version, out string? name))
            {
                if (!string.Equals(name, migration.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Migration {migration.Version} has an unexpected name.");
                }

                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO schema_migrations (version, name, applied_at_utc) VALUES ($version, $name, $time);";
            insert.Parameters.AddWithValue("$version", migration.Version);
            insert.Parameters.AddWithValue("$name", migration.Name);
            insert.Parameters.AddWithValue("$time", _timeProvider.GetUtcNow().ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(
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
