using Microsoft.Data.Sqlite;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Indexer.Tests.Infrastructure;

namespace PaymentSandbox.Indexer.Tests.Persistence;

public sealed class SqliteChainObservationStoreTests
{
    [Fact]
    public async Task InitializeAsync_AppliesStrictSchemaIdempotently()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);

        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.version, m.name, s.sql
            FROM schema_migrations AS m
            JOIN sqlite_schema AS s ON s.name = 'payment_recorded_observations'
            WHERE m.version = 1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("create_chain_observations", reader.GetString(1));
        Assert.Contains("STRICT", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));

        await reader.DisposeAsync();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        Assert.Equal(2, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task ConcurrentInitialize_AppliesMigrationExactlyOnce()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase first = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        IndexerDatabase second = IndexerTestData.CreateDatabase(temporary.DatabasePath);

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken),
            second.InitializeAsync(TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await first.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version IN (1, 2);";
        Assert.Equal(2, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task VersionTwoUpgrade_BackfillsExistingWeekEightBlocksAsCanonical()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteChainObservationStore(database);
        await store.CommitBatchAsync(
            null,
            IndexerTestData.Batch(),
            TestContext.Current.CancellationToken);

        // Recreate the state of a real Week 8 database: observations and its
        // checkpoint exist, but migration 2 and transition history do not.
        await using (SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE block_canonicality_transitions;
                DELETE FROM schema_migrations WHERE version = 2;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await database.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            IndexerTestData.Hash('1'),
            (await store.GetCanonicalBlockAsync(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                100,
                TestContext.Current.CancellationToken))!.Hash);
        await using SqliteConnection upgraded = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountAsync(upgraded, "block_canonicality_transitions"));
    }

    [Fact]
    public async Task CommitBatch_AtomicallyPersistsAndSurvivesStoreRestart()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var firstStore = new SqliteChainObservationStore(database);

        ObservationCommitResult committed = await firstStore.CommitBatchAsync(
            expectedPrevious: null,
            IndexerTestData.Batch(),
            TestContext.Current.CancellationToken);
        var restartedStore = new SqliteChainObservationStore(
            IndexerTestData.CreateDatabase(temporary.DatabasePath));
        ChainObservationCheckpoint? checkpoint = await restartedStore.GetCheckpointAsync(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            TestContext.Current.CancellationToken);

        Assert.Equal(ObservationCommitDisposition.Applied, committed.Disposition);
        Assert.Equal(committed.Checkpoint, checkpoint);
        Assert.Equal(101, checkpoint!.LastBlockNumber);
    }

    [Fact]
    public async Task SameUnknownOutcomeRetry_VerifiesRowsAndReturnsReplayed()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteChainObservationStore(database);
        ChainObservationBatch batch = IndexerTestData.Batch();
        await store.CommitBatchAsync(null, batch, TestContext.Current.CancellationToken);

        ChainObservationBatch retryAfterUnknownCommit = CopyBatchAt(
            batch,
            IndexerTestData.Now.AddSeconds(10));
        ObservationCommitResult replay = await store.CommitBatchAsync(
            expectedPrevious: null,
            retryAfterUnknownCommit,
            TestContext.Current.CancellationToken);

        Assert.Equal(ObservationCommitDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, replay.Checkpoint.Revision);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountAsync(connection, "observed_blocks"));
        Assert.Equal(1, await CountAsync(connection, "payment_recorded_observations"));
    }

    [Fact]
    public async Task ConcurrentSameBatch_CommitsOnceAndReplaysOnce()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        ChainObservationBatch batch = IndexerTestData.Batch();
        ChainObservationBatch independentlyObservedBatch = CopyBatchAt(
            batch,
            IndexerTestData.Now.AddSeconds(1));
        var first = new SqliteChainObservationStore(database);
        var second = new SqliteChainObservationStore(database);

        ObservationCommitResult[] results = await Task.WhenAll(
            first.CommitBatchAsync(null, batch, TestContext.Current.CancellationToken).AsTask(),
            second.CommitBatchAsync(
                null,
                independentlyObservedBatch,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, results.Count(result =>
            result.Disposition == ObservationCommitDisposition.Applied));
        Assert.Equal(1, results.Count(result =>
            result.Disposition == ObservationCommitDisposition.Replayed));
        Assert.All(results, result => Assert.Equal(1, result.Checkpoint.Revision));
    }

    [Fact]
    public async Task DifferentBatchWithStaleExpectedCheckpoint_IsRejected()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteChainObservationStore(database);
        await store.CommitBatchAsync(
            null,
            IndexerTestData.Batch(),
            TestContext.Current.CancellationToken);
        var different = new ChainObservationBatch(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            100,
            [new ObservedBlock(100, IndexerTestData.Hash('f'), IndexerTestData.Hash('0'))],
            [],
            IndexerTestData.Now);

        await Assert.ThrowsAsync<CheckpointConflictException>(() => store.CommitBatchAsync(
            expectedPrevious: null,
            different,
            TestContext.Current.CancellationToken).AsTask());

        ChainObservationCheckpoint? checkpoint = await store.GetCheckpointAsync(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexerTestData.Hash('2'), checkpoint!.LastBlockHash);
    }

    [Fact]
    public async Task ConcurrentSameReorganization_AppliesOnceAndRetainsBothForks()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var seedStore = new SqliteChainObservationStore(database);
        ObservationCommitResult initial = await seedStore.CommitBatchAsync(
            null,
            IndexerTestData.Batch(),
            TestContext.Current.CancellationToken);
        var ancestor = new ObservedBlock(100, IndexerTestData.Hash('1'), IndexerTestData.Hash('0'));
        var replacement = new ChainObservationBatch(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            100,
            [
                new ObservedBlock(101, IndexerTestData.Hash('e'), IndexerTestData.Hash('1')),
                new ObservedBlock(102, IndexerTestData.Hash('f'), IndexerTestData.Hash('e')),
            ],
            [],
            IndexerTestData.Now.AddMinutes(1));
        var first = new SqliteChainObservationStore(database);
        var second = new SqliteChainObservationStore(database);

        ObservationCommitResult[] results = await Task.WhenAll(
            first.CommitReorganizationAsync(
                initial.Checkpoint,
                ancestor,
                replacement,
                TestContext.Current.CancellationToken).AsTask(),
            second.CommitReorganizationAsync(
                initial.Checkpoint,
                ancestor,
                replacement,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, results.Count(result =>
            result.Disposition == ObservationCommitDisposition.Reorganized));
        Assert.Equal(1, results.Count(result =>
            result.Disposition == ObservationCommitDisposition.Replayed));
        Assert.All(results, result => Assert.Equal(2, result.Checkpoint.Revision));
        Assert.Equal(
            IndexerTestData.Hash('e'),
            (await seedStore.GetCanonicalBlockAsync(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                101,
                TestContext.Current.CancellationToken))!.Hash);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(4, await CountAsync(connection, "observed_blocks"));
        Assert.Equal(1, await CountAsync(connection, "payment_recorded_observations"));
        Assert.Equal(5, await CountAsync(connection, "block_canonicality_transitions"));
    }

    [Fact]
    public async Task ReorganizationWithNonHighestAncestor_IsRejectedWithoutTransitions()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteChainObservationStore(database);
        ObservationCommitResult initial = await store.CommitBatchAsync(
            null,
            IndexerTestData.Batch(),
            TestContext.Current.CancellationToken);
        var ancestor = new ObservedBlock(100, IndexerTestData.Hash('1'), IndexerTestData.Hash('0'));
        var unchangedSuffix = new ChainObservationBatch(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            100,
            [new ObservedBlock(101, IndexerTestData.Hash('2'), IndexerTestData.Hash('1'))],
            [],
            IndexerTestData.Now.AddMinutes(1));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CommitReorganizationAsync(
            initial.Checkpoint,
            ancestor,
            unchangedSuffix,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            initial.Checkpoint,
            await store.GetCheckpointAsync(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                TestContext.Current.CancellationToken));
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountAsync(connection, "block_canonicality_transitions"));
    }

    [Fact]
    public async Task Schema_RejectsNonCanonicalDirectObservation()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO observed_blocks (
                chain_id, router_address, block_number, block_hash,
                parent_hash, observed_at_utc)
            VALUES (
                '031337', '0x1111111111111111111111111111111111111111', 100,
                $blockHash, $parentHash, '2026-08-30T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$blockHash", IndexerTestData.Hash('1').Value);
        command.Parameters.AddWithValue("$parentHash", IndexerTestData.Hash('0').Value);

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task InitializeAsync_RejectsNewerUnknownSchemaVersion()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES (999, 'future_schema', '2026-08-30T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RejectsKnownVersionWithDifferentOwnerName()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary.DatabasePath)!);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = temporary.DatabasePath }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    applied_at_utc TEXT NOT NULL
                ) STRICT;
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES (1, 'owned_by_another_component', '2026-08-30T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("expected 'create_chain_observations'", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static ChainObservationBatch CopyBatchAt(
        ChainObservationBatch batch,
        DateTimeOffset observedAtUtc) =>
        new(
            batch.ChainId,
            batch.Router,
            batch.StartBlockNumber,
            batch.Blocks,
            batch.Payments,
            observedAtUtc);
}
