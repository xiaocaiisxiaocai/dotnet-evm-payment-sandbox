using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Tests.Infrastructure;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Ledger.Processing;

namespace PaymentSandbox.Finality.Tests.Integration;

public sealed class DeepReorganizationFinalityTests
{
    [Fact]
    public async Task QualifiedPayment_DeepReorganizationAppendsFinalityRevocation()
    {
        await using var temporary = new TemporaryFinalityDatabases();
        var time = new FinalityTestData.FixedTimeProvider(FinalityTestData.Now);
        var indexerDatabase = new IndexerDatabase(
            new IndexerDatabaseOptions(temporary.IndexerPath),
            time);
        var ledgerDatabase = new LedgerDatabase(
            new LedgerDatabaseOptions(temporary.LedgerPath),
            time);
        FinalityDatabase finalityDatabase = FinalityTestData.CreateDatabase(
            temporary.FinalityPath);
        await indexerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await ledgerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await finalityDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        var observations = new SqliteChainObservationStore(indexerDatabase);
        var ledger = new SqliteLedgerStore(ledgerDatabase);
        var ledgerProcessor = new CanonicalPaymentLedgerProcessor(
            new CanonicalPaymentLedgerPolicy(
                FinalityTestData.ChainId,
                FinalityTestData.Router),
            observations,
            ledger,
            time);
        var finalityStore = new SqliteFinalityStore(finalityDatabase);
        var finalityProcessor = new ConfirmationFinalityProcessor(
            FinalityTestData.Policy(requiredConfirmations: 3),
            observations,
            ledger,
            finalityStore,
            time);

        PaymentRecordedObservation payment = FinalityTestData.Payment();
        var initialBatch = new ChainObservationBatch(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            startBlockNumber: 100,
            [
                new ObservedBlock(100, FinalityTestData.Hash('1'), FinalityTestData.Hash('0')),
                new ObservedBlock(101, FinalityTestData.Hash('2'), FinalityTestData.Hash('1')),
                new ObservedBlock(102, FinalityTestData.Hash('3'), FinalityTestData.Hash('2')),
                new ObservedBlock(103, FinalityTestData.Hash('4'), FinalityTestData.Hash('3')),
            ],
            [payment],
            FinalityTestData.Now);
        ObservationCommitResult initial = await observations.CommitBatchAsync(
            null,
            initialBatch,
            TestContext.Current.CancellationToken);
        ChainObservationSnapshot initialSnapshot = await observations.GetCanonicalSnapshotAsync(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            TestContext.Current.CancellationToken);
        await ledgerProcessor.ProcessThroughTransitionAsync(
            initialSnapshot.CanonicalityHighWatermark,
            TestContext.Current.CancellationToken);
        long initialLedgerHighWatermark = await ledger.GetEntryHighWatermarkAsync(
            TestContext.Current.CancellationToken);

        FinalityEvaluationResult qualified = await finalityProcessor.EvaluateAsync(
            initialLedgerHighWatermark,
            initialSnapshot,
            TestContext.Current.CancellationToken);
        LedgerEntry effect = Assert.Single(await ledger.GetEntriesAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockHash,
            payment.TransactionHash,
            payment.LogIndex,
            TestContext.Current.CancellationToken));

        var ancestor = new ObservedBlock(
            100,
            FinalityTestData.Hash('1'),
            FinalityTestData.Hash('0'));
        var replacement = new ChainObservationBatch(
            FinalityTestData.ChainId,
            FinalityTestData.Router,
            startBlockNumber: 100,
            [
                new ObservedBlock(101, FinalityTestData.Hash('e'), FinalityTestData.Hash('1')),
                new ObservedBlock(102, FinalityTestData.Hash('f'), FinalityTestData.Hash('e')),
                new ObservedBlock(103, FinalityTestData.Hash('9'), FinalityTestData.Hash('f')),
            ],
            [],
            FinalityTestData.Now.AddMinutes(1));
        await observations.CommitReorganizationAsync(
            initial.Checkpoint,
            ancestor,
            replacement,
            TestContext.Current.CancellationToken);
        ChainObservationSnapshot reorganizedSnapshot =
            await observations.GetCanonicalSnapshotAsync(
                FinalityTestData.ChainId,
                FinalityTestData.Router,
                TestContext.Current.CancellationToken);
        await ledgerProcessor.ProcessThroughTransitionAsync(
            reorganizedSnapshot.CanonicalityHighWatermark,
            TestContext.Current.CancellationToken);
        long reorganizedLedgerHighWatermark = await ledger.GetEntryHighWatermarkAsync(
            TestContext.Current.CancellationToken);

        FinalityEvaluationResult revoked = await finalityProcessor.EvaluateAsync(
            reorganizedLedgerHighWatermark,
            reorganizedSnapshot,
            TestContext.Current.CancellationToken);
        IReadOnlyList<FinalityTransition> transitions =
            await finalityStore.GetTransitionsAsync(
                FinalityTestData.ChainId,
                FinalityTestData.Router,
                effect.EntryId,
                TestContext.Current.CancellationToken);

        Assert.Equal(1, qualified.QualificationCount);
        Assert.Equal(1, revoked.RevocationCount);
        Assert.Equal(4, initialSnapshot.CanonicalityHighWatermark);
        Assert.Equal(10, reorganizedSnapshot.CanonicalityHighWatermark);
        Assert.Equal(1, initialLedgerHighWatermark);
        Assert.Equal(2, reorganizedLedgerHighWatermark);
        Assert.Equal(
            [FinalityTransitionKind.ConfirmationQualified,
                FinalityTransitionKind.ConfirmationRevoked],
            transitions.Select(item => item.Kind));
        Assert.Equal(FinalityTransitionReason.LedgerEffectReversed, transitions[1].Reason);
        Assert.Equal(transitions[0].TransitionId, transitions[1].RevokesTransitionId);

        // The old source facts and provisional effect remain available. Finality
        // revocation is an additional explanation, never a historical delete.
        Assert.Equal(2, (await ledger.GetEntriesAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockHash,
            payment.TransactionHash,
            payment.LogIndex,
            TestContext.Current.CancellationToken)).Count);
        Assert.Single(await observations.GetPaymentsAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockNumber,
            payment.BlockHash,
            maxCount: 10,
            TestContext.Current.CancellationToken));
    }
}
