using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Finality.Tests.Infrastructure;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Finality.Tests.Evaluation;

public sealed class ConfirmationFinalityProcessorTests
{
    [Fact]
    public async Task Evaluate_CaughtUpSourcesApplyQualification()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        LedgerEntry effect = FinalityTestData.Effect();
        (ConfirmationFinalityProcessor processor, _, _) = await CreateProcessorAsync(
            temporary,
            FinalityTestData.Policy(),
            FinalityTestData.Snapshot(),
            FinalityTestData.LedgerCheckpoint(),
            [effect]);

        FinalityEvaluationResult result = await processor.EvaluateAsync(
            1,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken);

        Assert.Equal(FinalityEvaluationDisposition.Applied, result.Disposition);
        Assert.Equal(1, result.SourceLedgerEntryCount);
        Assert.Equal(1, result.QualificationCount);
        Assert.Equal(0, result.RevocationCount);
    }

    [Fact]
    public async Task SameExplicitTarget_ReturnsNoWorkWithoutReadingSourcesAgain()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        LedgerEntry effect = FinalityTestData.Effect();
        (ConfirmationFinalityProcessor processor, FakeObservationReader observations,
            FakeLedgerReader ledger) = await CreateProcessorAsync(
                temporary,
                FinalityTestData.Policy(),
                FinalityTestData.Snapshot(),
                FinalityTestData.LedgerCheckpoint(),
                [effect]);
        await processor.EvaluateAsync(
            1,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken);
        int snapshotReads = observations.SnapshotReads;
        int checkpointReads = ledger.CheckpointReads;
        int highWatermarkReads = ledger.HighWatermarkReads;
        int entryReads = ledger.EntryReads;

        FinalityEvaluationResult result = await processor.EvaluateAsync(
            1,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken);

        Assert.Equal(FinalityEvaluationDisposition.NoWork, result.Disposition);
        Assert.Equal(snapshotReads, observations.SnapshotReads);
        Assert.Equal(checkpointReads, ledger.CheckpointReads);
        Assert.Equal(highWatermarkReads, ledger.HighWatermarkReads);
        Assert.Equal(entryReads, ledger.EntryReads);
    }

    [Fact]
    public async Task ChangedIndexerSnapshot_IsRejectedBeforeLedgerReads()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        ChainObservationSnapshot current = FinalityTestData.Snapshot(
            headBlockNumber: 104,
            headHash: '5',
            checkpointRevision: 2,
            highWatermark: 5);
        (ConfirmationFinalityProcessor processor, _, FakeLedgerReader ledger) =
            await CreateProcessorAsync(
                temporary,
                FinalityTestData.Policy(),
                current,
                FinalityTestData.LedgerCheckpoint(5, 2),
                [FinalityTestData.Effect()]);

        await Assert.ThrowsAsync<FinalityEvaluationException>(() => processor.EvaluateAsync(
            1,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, ledger.CheckpointReads);
        Assert.Equal(0, ledger.EntryReads);
    }

    [Fact]
    public async Task LedgerBehindIndexer_IsRejectedBeforeEntriesAreRead()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        (ConfirmationFinalityProcessor processor, _, FakeLedgerReader ledger) =
            await CreateProcessorAsync(
                temporary,
                FinalityTestData.Policy(),
                FinalityTestData.Snapshot(highWatermark: 4),
                FinalityTestData.LedgerCheckpoint(lastSourceTransitionId: 3),
                [FinalityTestData.Effect()]);

        FinalityEvaluationException exception = await Assert.ThrowsAsync<FinalityEvaluationException>(
            () => processor.EvaluateAsync(
                1,
                FinalityTestData.Snapshot(highWatermark: 4),
                TestContext.Current.CancellationToken));

        Assert.Contains("not caught up", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, ledger.EntryReads);
    }

    [Fact]
    public async Task CallerMustSelectExactLedgerHighWatermark()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        (ConfirmationFinalityProcessor processor, _, FakeLedgerReader ledger) =
            await CreateProcessorAsync(
                temporary,
                FinalityTestData.Policy(),
                FinalityTestData.Snapshot(),
                FinalityTestData.LedgerCheckpoint(),
                [FinalityTestData.Effect()]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => processor.EvaluateAsync(
            0,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken));

        Assert.Equal(1, ledger.HighWatermarkReads);
        Assert.Equal(0, ledger.EntryReads);
    }

    [Fact]
    public async Task LedgerEntryLimit_FailsBeforeFinalityCommit()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        LedgerEntry first = FinalityTestData.Effect(entryId: 1);
        LedgerEntry second = FinalityTestData.Effect(
            entryId: 2,
            transactionHash: 'd',
            logIndex: 4);
        (ConfirmationFinalityProcessor processor, _, _) = await CreateProcessorAsync(
            temporary,
            FinalityTestData.Policy(maxEntries: 1),
            FinalityTestData.Snapshot(),
            FinalityTestData.LedgerCheckpoint(),
            [first, second]);

        await Assert.ThrowsAsync<FinalityEvaluationException>(() => processor.EvaluateAsync(
            2,
            FinalityTestData.Snapshot(),
            TestContext.Current.CancellationToken));

        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);
        var store = new SqliteFinalityStore(database);
        Assert.Null(await store.GetCheckpointAsync(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            TestContext.Current.CancellationToken));
    }

    private static async Task<(
        ConfirmationFinalityProcessor Processor,
        FakeObservationReader Observations,
        FakeLedgerReader Ledger)> CreateProcessorAsync(
            TemporaryFinalityDatabases temporary,
            ConfirmationFinalityPolicy policy,
            ChainObservationSnapshot snapshot,
            LedgerCheckpoint ledgerCheckpoint,
            IReadOnlyList<LedgerEntry> entries)
    {
        FinalityDatabase database = FinalityTestData.CreateDatabase(temporary.FinalityPath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var observations = new FakeObservationReader(snapshot);
        var ledger = new FakeLedgerReader(ledgerCheckpoint, entries);
        var processor = new ConfirmationFinalityProcessor(
            policy,
            observations,
            ledger,
            new SqliteFinalityStore(database),
            new FinalityTestData.FixedTimeProvider(FinalityTestData.Now));
        return (processor, observations, ledger);
    }
}
