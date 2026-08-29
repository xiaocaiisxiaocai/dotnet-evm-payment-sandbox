using Microsoft.Data.Sqlite;
using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Finality.Tests.Infrastructure;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Finality.Tests.Persistence;

public sealed class SqliteFinalityStoreTests
{
    [Fact]
    public async Task InitializeAsync_AppliesStrictSchemaIdempotently()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);

        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.name, s.sql
            FROM schema_migrations AS m
            JOIN sqlite_schema AS s ON s.name = 'payment_finality_transitions'
            WHERE m.version = 1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("create_confirmation_finality_projection", reader.GetString(0));
        Assert.Contains("STRICT", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BelowThreshold_AdvancesCheckpointWithoutQualification()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        ConfirmationFinalityPolicy policy = FinalityTestData.Policy(requiredConfirmations: 3);
        LedgerEntry effect = FinalityTestData.Effect(blockNumber: 101);
        FinalityEvaluationBatch batch = FinalityTestData.Batch(
            policy,
            1,
            FinalityTestData.LedgerCheckpoint(),
            FinalityTestData.Snapshot(headBlockNumber: 102, headHash: '3'),
            [effect]);

        FinalityCommitResult result = await store.CommitAsync(
            null,
            batch,
            TestContext.Current.CancellationToken);

        Assert.Equal(FinalityCommitDisposition.Applied, result.Disposition);
        Assert.Equal(0, result.QualificationCount);
        Assert.Empty(await GetTransitionsAsync(store, effect.EntryId));
    }

    [Fact]
    public async Task InclusiveThreshold_AppendsQualificationWithExactCount()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        LedgerEntry effect = FinalityTestData.Effect(blockNumber: 101);

        FinalityCommitResult result = await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                FinalityTestData.Policy(3),
                1,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(headBlockNumber: 103),
                [effect]),
            TestContext.Current.CancellationToken);
        FinalityTransition transition = Assert.Single(await GetTransitionsAsync(store, 1));

        Assert.Equal(1, result.QualificationCount);
        Assert.Equal(FinalityTransitionKind.ConfirmationQualified, transition.Kind);
        Assert.Equal(3, transition.ConfirmationCount);
        Assert.Equal(FinalityTransitionReason.ConfirmationThresholdReached, transition.Reason);
    }

    [Fact]
    public async Task HeadRegression_AppendsRevocationAndLaterRequalification()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        ConfirmationFinalityPolicy policy = FinalityTestData.Policy(3);
        LedgerEntry effect = FinalityTestData.Effect(blockNumber: 101);
        FinalityCommitResult first = await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(4, 1),
                FinalityTestData.Snapshot(103, '4', 1, 4),
                [effect]),
            TestContext.Current.CancellationToken);
        FinalityCommitResult second = await store.CommitAsync(
            first.Checkpoint,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(5, 2),
                FinalityTestData.Snapshot(102, '3', 2, 5),
                []),
            TestContext.Current.CancellationToken);
        FinalityCommitResult third = await store.CommitAsync(
            second.Checkpoint,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(6, 3),
                FinalityTestData.Snapshot(103, '4', 3, 6),
                []),
            TestContext.Current.CancellationToken);
        IReadOnlyList<FinalityTransition> transitions = await GetTransitionsAsync(store, 1);

        Assert.Equal(1, second.RevocationCount);
        Assert.Equal(1, third.QualificationCount);
        Assert.Equal(
            [
                FinalityTransitionKind.ConfirmationQualified,
                FinalityTransitionKind.ConfirmationRevoked,
                FinalityTransitionKind.ConfirmationQualified,
            ],
            transitions.Select(item => item.Kind));
        Assert.Equal(FinalityTransitionReason.ConfirmationThresholdLost, transitions[1].Reason);
        Assert.Equal(transitions[0].TransitionId, transitions[1].RevokesTransitionId);
    }

    [Fact]
    public async Task LedgerReversal_AppendsRevocationLinkedToQualification()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        ConfirmationFinalityPolicy policy = FinalityTestData.Policy(3);
        LedgerEntry effect = FinalityTestData.Effect();
        FinalityCommitResult first = await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(4, 1),
                FinalityTestData.Snapshot(103, '4', 1, 4),
                [effect]),
            TestContext.Current.CancellationToken);
        LedgerEntry reversal = FinalityTestData.Reversal(effect);

        FinalityCommitResult second = await store.CommitAsync(
            first.Checkpoint,
            FinalityTestData.Batch(
                policy,
                2,
                FinalityTestData.LedgerCheckpoint(5, 2),
                FinalityTestData.Snapshot(103, '9', 2, 5),
                [reversal]),
            TestContext.Current.CancellationToken);
        IReadOnlyList<FinalityTransition> transitions = await GetTransitionsAsync(store, 1);

        Assert.Equal(1, second.RevocationCount);
        Assert.Equal(FinalityTransitionReason.LedgerEffectReversed, transitions[1].Reason);
        Assert.Equal(transitions[0].TransitionId, transitions[1].RevokesTransitionId);
    }

    [Fact]
    public async Task EffectAlreadyReversedBeforeFirstEvaluation_NeverQualifies()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        LedgerEntry effect = FinalityTestData.Effect();
        LedgerEntry reversal = FinalityTestData.Reversal(effect);

        FinalityCommitResult result = await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                FinalityTestData.Policy(1),
                2,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                [effect, reversal]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.QualificationCount);
        Assert.Equal(0, result.RevocationCount);
        Assert.Empty(await GetTransitionsAsync(store, effect.EntryId));
    }

    [Fact]
    public async Task UnknownOutcomeRetry_IgnoresNewLocalTimeAndReturnsReplayed()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        ConfirmationFinalityPolicy policy = FinalityTestData.Policy(3);
        LedgerEntry effect = FinalityTestData.Effect();
        FinalityEvaluationBatch original = FinalityTestData.Batch(
            policy,
            1,
            FinalityTestData.LedgerCheckpoint(),
            FinalityTestData.Snapshot(),
            [effect]);
        await store.CommitAsync(null, original, TestContext.Current.CancellationToken);
        FinalityEvaluationBatch retry = FinalityTestData.Batch(
            policy,
            1,
            FinalityTestData.LedgerCheckpoint(),
            FinalityTestData.Snapshot(),
            [effect],
            FinalityTestData.Now.AddHours(1));

        FinalityCommitResult replay = await store.CommitAsync(
            null,
            retry,
            TestContext.Current.CancellationToken);

        Assert.Equal(original.Fingerprint, retry.Fingerprint);
        Assert.Equal(FinalityCommitDisposition.Replayed, replay.Disposition);
        Assert.Single(await GetTransitionsAsync(store, 1));
    }

    [Fact]
    public async Task SameTargetWithChangedLedgerFact_IsCheckpointConflict()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        ConfirmationFinalityPolicy policy = FinalityTestData.Policy();
        LedgerEntry effect = FinalityTestData.Effect();
        await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                [effect]),
            TestContext.Current.CancellationToken);
        LedgerEntry changed = effect with { Amount = new(9_999) };

        await Assert.ThrowsAsync<FinalityCheckpointConflictException>(() => store.CommitAsync(
            null,
            FinalityTestData.Batch(
                policy,
                1,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                [changed]),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ConcurrentSameEvaluation_CommitsOnceAndReplaysOnce()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var first = new SqliteFinalityStore(database);
        var second = new SqliteFinalityStore(database);
        FinalityEvaluationBatch batch = FinalityTestData.Batch(
            FinalityTestData.Policy(),
            1,
            FinalityTestData.LedgerCheckpoint(),
            FinalityTestData.Snapshot(),
            [FinalityTestData.Effect()]);

        FinalityCommitResult[] results = await Task.WhenAll(
            first.CommitAsync(null, batch, TestContext.Current.CancellationToken).AsTask(),
            second.CommitAsync(null, batch, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, results.Count(item => item.Disposition == FinalityCommitDisposition.Applied));
        Assert.Equal(1, results.Count(item => item.Disposition == FinalityCommitDisposition.Replayed));
    }

    [Fact]
    public async Task EffectLimitFailure_RollsBackSourceRowsAndCheckpoint()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteFinalityStore(database);
        LedgerEntry first = FinalityTestData.Effect(entryId: 1);
        LedgerEntry second = FinalityTestData.Effect(
            entryId: 2,
            transactionHash: 'd',
            logIndex: 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(
            null,
            FinalityTestData.Batch(
                FinalityTestData.Policy(maxEffects: 1),
                2,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                [first, second]),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Null(await store.GetCheckpointAsync(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            TestContext.Current.CancellationToken));
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM finality_source_ledger_entries;";
        Assert.Equal(0, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task PolicyMeaningCannotChangeInsideExistingStream()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        SqliteFinalityStore store = await CreateStoreAsync(temporary);
        LedgerEntry effect = FinalityTestData.Effect();
        FinalityCommitResult first = await store.CommitAsync(
            null,
            FinalityTestData.Batch(
                FinalityTestData.Policy(3),
                1,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                [effect]),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(
            first.Checkpoint,
            FinalityTestData.Batch(
                FinalityTestData.Policy(4),
                1,
                FinalityTestData.LedgerCheckpoint(),
                FinalityTestData.Snapshot(),
                []),
            TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task<SqliteFinalityStore> CreateStoreAsync(
        TemporaryFinalityDatabases temporary)
    {
        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        return new SqliteFinalityStore(database);
    }

    private static ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsAsync(
        SqliteFinalityStore store,
        long effectEntryId) =>
        store.GetTransitionsAsync(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            effectEntryId,
            TestContext.Current.CancellationToken);
}
