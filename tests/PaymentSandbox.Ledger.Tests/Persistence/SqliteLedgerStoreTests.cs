using Microsoft.Data.Sqlite;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Ledger.Tests.Infrastructure;

namespace PaymentSandbox.Ledger.Tests.Persistence;

public sealed class SqliteLedgerStoreTests
{
    [Fact]
    public async Task InitializeAsync_AppliesStrictSchemaIdempotently()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        LedgerDatabase database = LedgerTestData.CreateDatabase(temporary.DatabasePath);

        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.name, s.sql
            FROM schema_migrations AS m
            JOIN sqlite_schema AS s ON s.name = 'canonical_payment_ledger_entries'
            WHERE m.version = 1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("create_canonical_payment_ledger", reader.GetString(0));
        Assert.Contains("STRICT", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentInitialize_AppliesMigrationExactlyOnce()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        LedgerDatabase first = LedgerTestData.CreateDatabase(temporary.DatabasePath);
        LedgerDatabase second = LedgerTestData.CreateDatabase(temporary.DatabasePath);

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken),
            second.InitializeAsync(TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await first.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountAsync(connection, "schema_migrations"));
    }

    [Fact]
    public async Task CanonicalThenNoncanonical_AppendsLinkedReversalAndRetainsHistory()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, _) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        var canonical = new CanonicalPaymentChange(
            LedgerTestData.Transition(1),
            [payment]);
        LedgerCommitResult first = await store.CommitAsync(
            null,
            LedgerTestData.Batch(1, [canonical]),
            TestContext.Current.CancellationToken);
        var noncanonical = new CanonicalPaymentChange(
            LedgerTestData.Transition(
                2,
                BlockCanonicality.Noncanonical,
                checkpointRevision: 2),
            [payment]);

        LedgerCommitResult second = await store.CommitAsync(
            first.Checkpoint,
            LedgerTestData.Batch(2, [noncanonical], LedgerTestData.Now.AddMinutes(1)),
            TestContext.Current.CancellationToken);
        IReadOnlyList<LedgerEntry> entries = await GetEntriesAsync(store, payment);

        Assert.Equal(LedgerCommitDisposition.Applied, second.Disposition);
        Assert.Equal(2, second.Checkpoint.Revision);
        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(LedgerEntryKind.CanonicalPayment, entry.Kind);
                Assert.Null(entry.ReversesEntryId);
            },
            entry =>
            {
                Assert.Equal(LedgerEntryKind.CanonicalPaymentReversal, entry.Kind);
                Assert.Equal(entries[0].EntryId, entry.ReversesEntryId);
            });
    }

    [Fact]
    public async Task RecanonicalizationAfterReversal_StartsASecondEffectGeneration()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, _) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        LedgerCommitResult first = await CommitChangeAsync(
            store,
            null,
            1,
            BlockCanonicality.Canonical,
            payment);
        LedgerCommitResult second = await CommitChangeAsync(
            store,
            first.Checkpoint,
            2,
            BlockCanonicality.Noncanonical,
            payment);

        await CommitChangeAsync(
            store,
            second.Checkpoint,
            3,
            BlockCanonicality.Canonical,
            payment);
        IReadOnlyList<LedgerEntry> entries = await GetEntriesAsync(store, payment);

        Assert.Equal(
            [
                LedgerEntryKind.CanonicalPayment,
                LedgerEntryKind.CanonicalPaymentReversal,
                LedgerEntryKind.CanonicalPayment,
            ],
            entries.Select(entry => entry.Kind));
        Assert.Null(entries[2].ReversesEntryId);
    }

    [Fact]
    public async Task ReversalWithoutActiveEffect_RollsBackEntriesAndCheckpoint()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, LedgerDatabase database) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        var invalid = new CanonicalPaymentChange(
            LedgerTestData.Transition(1, BlockCanonicality.Noncanonical),
            [payment]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(
            null,
            LedgerTestData.Batch(1, [invalid]),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Null(await store.GetCheckpointAsync(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            TestContext.Current.CancellationToken));
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, await CountAsync(connection, "canonical_payment_ledger_entries"));
    }

    [Fact]
    public async Task UnknownOutcomeRetry_IgnoresNewLocalTimeAndReturnsReplayed()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, _) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        var change = new CanonicalPaymentChange(LedgerTestData.Transition(1), [payment]);
        CanonicalPaymentBatch original = LedgerTestData.Batch(1, [change]);
        await store.CommitAsync(null, original, TestContext.Current.CancellationToken);

        // RecordedAtUtc belongs to this database, not to the Indexer source fact.
        // A retry after an unknown commit therefore receives the same fingerprint.
        CanonicalPaymentBatch retry = LedgerTestData.Batch(
            1,
            [change],
            LedgerTestData.Now.AddHours(1));
        LedgerCommitResult replay = await store.CommitAsync(
            null,
            retry,
            TestContext.Current.CancellationToken);

        Assert.Equal(original.Fingerprint, retry.Fingerprint);
        Assert.Equal(LedgerCommitDisposition.Replayed, replay.Disposition);
        Assert.Single(await GetEntriesAsync(store, payment));
    }

    [Fact]
    public async Task ConcurrentSameBatch_CommitsOnceAndReplaysOnce()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore first, LedgerDatabase database) = await CreateStoreAsync(temporary);
        var second = new SqliteLedgerStore(database);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        var change = new CanonicalPaymentChange(LedgerTestData.Transition(1), [payment]);
        CanonicalPaymentBatch firstObservation = LedgerTestData.Batch(1, [change]);
        CanonicalPaymentBatch independentObservation = LedgerTestData.Batch(
            1,
            [change],
            LedgerTestData.Now.AddSeconds(30));

        LedgerCommitResult[] results = await Task.WhenAll(
            first.CommitAsync(
                null,
                firstObservation,
                TestContext.Current.CancellationToken).AsTask(),
            second.CommitAsync(
                null,
                independentObservation,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, results.Count(item => item.Disposition == LedgerCommitDisposition.Applied));
        Assert.Equal(1, results.Count(item => item.Disposition == LedgerCommitDisposition.Replayed));
        Assert.Single(await GetEntriesAsync(first, payment));
    }

    [Fact]
    public async Task SameTargetWithChangedSourceFact_IsRejectedAsCheckpointConflict()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, _) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        BlockCanonicalityTransition transition = LedgerTestData.Transition(1);
        await store.CommitAsync(
            null,
            LedgerTestData.Batch(1, [new(transition, [payment])]),
            TestContext.Current.CancellationToken);
        PaymentRecordedObservation changedAmount = LedgerTestData.Payment(amount: 9_999);

        await Assert.ThrowsAsync<LedgerCheckpointConflictException>(() => store.CommitAsync(
            null,
            LedgerTestData.Batch(1, [new(transition, [changedAmount])]),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EmptySourceInterval_AdvancesCheckpointWithoutFabricatingEntries()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, LedgerDatabase database) = await CreateStoreAsync(temporary);

        LedgerCommitResult result = await store.CommitAsync(
            null,
            LedgerTestData.Batch(7, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.Checkpoint.LastSourceTransitionId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, await CountAsync(connection, "canonical_payment_ledger_entries"));
    }

    [Fact]
    public async Task EntryReadBoundary_UsesGlobalCursorAndStreamOrder()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, _) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        LedgerCommitResult first = await CommitChangeAsync(
            store,
            null,
            1,
            BlockCanonicality.Canonical,
            payment);
        await CommitChangeAsync(
            store,
            first.Checkpoint,
            2,
            BlockCanonicality.Noncanonical,
            payment);

        LedgerReadSnapshot snapshot = await store.GetSnapshotAsync(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            TestContext.Current.CancellationToken);
        IReadOnlyList<LedgerEntry> entries = await store.GetEntriesByPaymentIdAsync(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            payment.PaymentId,
            throughEntryId: snapshot.EntryHighWatermark,
            maxCount: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.EntryHighWatermark);
        Assert.Equal(2, snapshot.Checkpoint!.Revision);
        Assert.Equal([1L, 2L], entries.Select(entry => entry.EntryId));
        Assert.Equal(
            [LedgerEntryKind.CanonicalPayment, LedgerEntryKind.CanonicalPaymentReversal],
            entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task Schema_RejectsReversalLinkedToDifferentOccurrence()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, LedgerDatabase database) = await CreateStoreAsync(temporary);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        await CommitChangeAsync(
            store,
            null,
            1,
            BlockCanonicality.Canonical,
            payment);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO canonical_payment_ledger_entries (
                chain_id, router_address, kind, source_transition_id,
                source_checkpoint_revision, block_number, block_hash,
                transaction_hash, log_index, payment_id, payer_address,
                token_address, merchant_address, amount_raw, reverses_entry_id,
                source_changed_at_utc, recorded_at_utc)
            SELECT chain_id, router_address, 'canonical_payment_reversal', 2,
                   2, block_number, block_hash, transaction_hash, log_index + 1,
                   payment_id, payer_address, token_address, merchant_address,
                   amount_raw, entry_id, source_changed_at_utc, recorded_at_utc
            FROM canonical_payment_ledger_entries
            WHERE kind = 'canonical_payment';
            """;

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("invalid ledger reversal reference", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<(SqliteLedgerStore Store, LedgerDatabase Database)> CreateStoreAsync(
        TemporaryLedgerDatabase temporary)
    {
        LedgerDatabase database = LedgerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        return (new SqliteLedgerStore(database), database);
    }

    private static async Task<LedgerCommitResult> CommitChangeAsync(
        SqliteLedgerStore store,
        LedgerCheckpoint? previous,
        long transitionId,
        BlockCanonicality canonicality,
        PaymentRecordedObservation payment)
    {
        var transition = LedgerTestData.Transition(
            transitionId,
            canonicality,
            checkpointRevision: transitionId);
        return await store.CommitAsync(
            previous,
            LedgerTestData.Batch(
                transitionId,
                [new CanonicalPaymentChange(transition, [payment])],
                LedgerTestData.Now.AddSeconds(transitionId)),
            TestContext.Current.CancellationToken);
    }

    private static ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        SqliteLedgerStore store,
        PaymentRecordedObservation payment) =>
        store.GetEntriesAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockHash,
            payment.TransactionHash,
            payment.LogIndex,
            TestContext.Current.CancellationToken);

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
