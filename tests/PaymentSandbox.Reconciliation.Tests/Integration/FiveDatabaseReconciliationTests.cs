using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Api.Persistence;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Ledger.Processing;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Persistence;
using PaymentSandbox.Reconciliation.Reports;
using PaymentSandbox.Reconciliation.Tests.Infrastructure;

namespace PaymentSandbox.Reconciliation.Tests.Integration;

public sealed class FiveDatabaseReconciliationTests
{
    [Fact]
    public async Task QualifiedPaymentThenDeepReorganization_AppendsExplainableReport()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        var time = new ReconciliationTestData.FixedTimeProvider(ReconciliationTestData.Now);
        var intentDatabase = new PaymentIntentDatabase(
            new PaymentIntentDatabaseOptions(temporary.IntentPath), time);
        var indexerDatabase = new IndexerDatabase(
            new IndexerDatabaseOptions(temporary.IndexerPath), time);
        var ledgerDatabase = new LedgerDatabase(
            new LedgerDatabaseOptions(temporary.LedgerPath), time);
        var finalityDatabase = new FinalityDatabase(
            new FinalityDatabaseOptions(temporary.FinalityPath), time);
        ReconciliationDatabase reconciliationDatabase =
            ReconciliationTestData.Database(temporary.DatabasePath);
        await intentDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await indexerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await ledgerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await finalityDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await reconciliationDatabase.InitializeAsync(TestContext.Current.CancellationToken);

        var intents = new SqlitePaymentIntentStore(intentDatabase);
        Assert.True(IdempotencyKey.TryParse("five-db-integration", out IdempotencyKey? key));
        await intents.CreateOrGetAsync(
            key,
            ReconciliationTestData.Intent(),
            TestContext.Current.CancellationToken);
        var observations = new SqliteChainObservationStore(indexerDatabase);
        var ledger = new SqliteLedgerStore(ledgerDatabase);
        var ledgerProcessor = new CanonicalPaymentLedgerProcessor(
            new CanonicalPaymentLedgerPolicy(ReconciliationTestData.ChainId, ReconciliationTestData.Router),
            observations, ledger, time);
        var finality = new SqliteFinalityStore(finalityDatabase);
        var finalityProcessor = new ConfirmationFinalityProcessor(
            new ConfirmationFinalityPolicy(
                ReconciliationTestData.ChainId, ReconciliationTestData.Router,
                "confirmations-v1", requiredConfirmations: 3),
            observations, ledger, finality, time);
        var reports = new SqliteReconciliationStore(reconciliationDatabase);
        var reconciliation = new PaymentReconciliationProcessor(
            ReconciliationTestData.Policy(), intents, ledger, finality, reports, time);

        var payment = new PaymentRecordedObservation(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router, 101,
            ReconciliationTestData.Hash('2'), ReconciliationTestData.Hash('a'), 1,
            ReconciliationTestData.PaymentId,
            PaymentSandbox.Domain.Evm.EvmAddress.Parse("0x4444444444444444444444444444444444444444"),
            ReconciliationTestData.Token, ReconciliationTestData.Merchant,
            new PaymentSandbox.Domain.Payments.RawTokenAmount(1_250_000));
        var initialBatch = new ChainObservationBatch(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router, 100,
            [
                new ObservedBlock(100, ReconciliationTestData.Hash('1'), ReconciliationTestData.Hash('0')),
                new ObservedBlock(101, ReconciliationTestData.Hash('2'), ReconciliationTestData.Hash('1')),
                new ObservedBlock(102, ReconciliationTestData.Hash('3'), ReconciliationTestData.Hash('2')),
                new ObservedBlock(103, ReconciliationTestData.Hash('4'), ReconciliationTestData.Hash('3')),
            ],
            [payment], ReconciliationTestData.Now);
        ObservationCommitResult initial = await observations.CommitBatchAsync(
            null, initialBatch, TestContext.Current.CancellationToken);
        ChainObservationSnapshot initialChain = await observations.GetCanonicalSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        await ledgerProcessor.ProcessThroughTransitionAsync(
            initialChain.CanonicalityHighWatermark, TestContext.Current.CancellationToken);
        await finalityProcessor.EvaluateAsync(
            await ledger.GetEntryHighWatermarkAsync(TestContext.Current.CancellationToken),
            initialChain, TestContext.Current.CancellationToken);

        PaymentIntentReadSnapshot intentSnapshot = await intents.GetSnapshotAsync(
            ReconciliationTestData.PaymentId, TestContext.Current.CancellationToken);
        PaymentSandbox.Ledger.Entries.LedgerReadSnapshot firstLedger = await ledger.GetSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        FinalityReadSnapshot firstFinality = await finality.GetSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        ReconciliationCommitResult matched = await reconciliation.ReconcileAsync(
            ReconciliationTestData.PaymentId, intentSnapshot, firstLedger, firstFinality,
            TestContext.Current.CancellationToken);

        var replacement = new ChainObservationBatch(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router, 100,
            [
                new ObservedBlock(101, ReconciliationTestData.Hash('e'), ReconciliationTestData.Hash('1')),
                new ObservedBlock(102, ReconciliationTestData.Hash('f'), ReconciliationTestData.Hash('e')),
                new ObservedBlock(103, ReconciliationTestData.Hash('9'), ReconciliationTestData.Hash('f')),
            ], [], ReconciliationTestData.Now.AddMinutes(1));
        await observations.CommitReorganizationAsync(
            initial.Checkpoint,
            new ObservedBlock(100, ReconciliationTestData.Hash('1'), ReconciliationTestData.Hash('0')),
            replacement,
            TestContext.Current.CancellationToken);
        ChainObservationSnapshot reorganizedChain = await observations.GetCanonicalSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        await ledgerProcessor.ProcessThroughTransitionAsync(
            reorganizedChain.CanonicalityHighWatermark, TestContext.Current.CancellationToken);
        await finalityProcessor.EvaluateAsync(
            await ledger.GetEntryHighWatermarkAsync(TestContext.Current.CancellationToken),
            reorganizedChain, TestContext.Current.CancellationToken);
        PaymentSandbox.Ledger.Entries.LedgerReadSnapshot secondLedger = await ledger.GetSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        FinalityReadSnapshot secondFinality = await finality.GetSnapshotAsync(
            ReconciliationTestData.ChainId, ReconciliationTestData.Router,
            TestContext.Current.CancellationToken);
        ReconciliationCommitResult reversed = await reconciliation.ReconcileAsync(
            ReconciliationTestData.PaymentId, intentSnapshot, secondLedger, secondFinality,
            TestContext.Current.CancellationToken);

        Assert.True(matched.Report.IsConsistent);
        Assert.False(reversed.Report.IsConsistent);
        Assert.Contains(ReconciliationDiscrepancyCode.ActivePaymentMissing, reversed.Report.Discrepancies);
        Assert.Contains(ReconciliationDiscrepancyCode.ReversedPaymentHistory, reversed.Report.Discrepancies);
        Assert.Contains(ReconciliationDiscrepancyCode.AmountUnderpaid, reversed.Report.Discrepancies);
        IReadOnlyList<ReconciliationReport> history = await reports.GetReportsAsync(
            ReconciliationTestData.PaymentId, 10, TestContext.Current.CancellationToken);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].IsConsistent);
        Assert.False(history[1].IsConsistent);
    }
}
