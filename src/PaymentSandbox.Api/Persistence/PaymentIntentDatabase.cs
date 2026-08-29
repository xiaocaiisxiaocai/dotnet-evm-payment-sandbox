using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Api.Persistence;

/// <summary>Opens configured SQLite connections and applies owned migrations.</summary>
public sealed class PaymentIntentDatabase
{
    private readonly PaymentIntentDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;

    public PaymentIntentDatabase(
        PaymentIntentDatabaseOptions options,
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
        }.ToString();
    }

    public string DatabasePath => _options.DatabasePath;

    /// <summary>Creates the database directory and atomically advances its schema.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_options.DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The SQLite database path has no parent directory.");
        }

        Directory.CreateDirectory(directory);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction: null, "PRAGMA journal_mode = WAL;", cancellationToken);

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

        Dictionary<long, string> appliedMigrations = await ReadAppliedMigrationsAsync(
            connection,
            transaction,
            cancellationToken);
        long latestKnownVersion = PaymentIntentDatabaseMigrations.All[^1].Version;
        long unsupportedVersion = appliedMigrations.Keys.FirstOrDefault(
            version => version > latestKnownVersion);
        if (unsupportedVersion > 0)
        {
            throw new InvalidOperationException(
                $"Database schema version {unsupportedVersion} is newer than supported version {latestKnownVersion}.");
        }

        foreach (PaymentIntentDatabaseMigration expected in PaymentIntentDatabaseMigrations.All)
        {
            if (appliedMigrations.TryGetValue(expected.Version, out string? appliedName) &&
                !string.Equals(appliedName, expected.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database migration {expected.Version} is named '{appliedName}', expected '{expected.Name}'.");
            }
        }

        foreach (PaymentIntentDatabaseMigration migration in PaymentIntentDatabaseMigrations.All)
        {
            if (appliedMigrations.ContainsKey(migration.Version))
            {
                continue;
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                migration.Sql,
                cancellationToken);

            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText =
                """
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES ($version, $name, $appliedAtUtc);
                """;
            insertMigration.Parameters.AddWithValue("$version", migration.Version);
            insertMigration.Parameters.AddWithValue("$name", migration.Name);
            insertMigration.Parameters.AddWithValue(
                "$appliedAtUtc",
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Opens one operation-scoped connection with bounded lock waiting.</summary>
    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                transaction: null,
                "PRAGMA busy_timeout = 5000;",
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
        await using var command = connection.CreateCommand();
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
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
