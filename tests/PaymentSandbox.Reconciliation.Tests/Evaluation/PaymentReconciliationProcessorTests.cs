using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Persistence;
using PaymentSandbox.Reconciliation.Policy;
using PaymentSandbox.Reconciliation.Tests.Infrastructure;

namespace PaymentSandbox.Reconciliation.Tests.Evaluation;

public sealed class PaymentReconciliationProcessorTests
{
    [Fact]
    public async Task ExactSnapshots_CommitReport()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var effect = ReconciliationTestData.Effect();
        ReconciliationEvaluation seed = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);
        var intents = new FakeIntentReader(seed.IntentSnapshot);
        var ledger = new FakeLedgerReader(seed.LedgerSnapshot, [effect]);
        var finality = new FakeFinalityReader(seed.FinalitySnapshot, seed.FinalityTransitions);
        var processor = new PaymentReconciliationProcessor(
            ReconciliationTestData.Policy(), intents, ledger, finality,
            new SqliteReconciliationStore(database),
            new ReconciliationTestData.FixedTimeProvider(ReconciliationTestData.Now));

        ReconciliationCommitResult result = await processor.ReconcileAsync(
            ReconciliationTestData.PaymentId, seed.IntentSnapshot, seed.LedgerSnapshot,
            seed.FinalitySnapshot, TestContext.Current.CancellationToken);

        Assert.Equal(ReconciliationCommitDisposition.Applied, result.Disposition);
        Assert.True(result.Report.IsConsistent);
    }

    [Fact]
    public async Task ChangedIntentSnapshot_FailsBeforeOtherSources()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var effect = ReconciliationTestData.Effect();
        ReconciliationEvaluation seed = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);
        var changed = new PaymentIntentReadSnapshot(
            ReconciliationTestData.PaymentId, seed.IntentSnapshot.Intent, 2, 1);
        var ledger = new FakeLedgerReader(seed.LedgerSnapshot, [effect]);
        var processor = new PaymentReconciliationProcessor(
            ReconciliationTestData.Policy(), new FakeIntentReader(changed), ledger,
            new FakeFinalityReader(seed.FinalitySnapshot, seed.FinalityTransitions),
            new SqliteReconciliationStore(database),
            new ReconciliationTestData.FixedTimeProvider(ReconciliationTestData.Now));

        await Assert.ThrowsAsync<ReconciliationException>(() => processor.ReconcileAsync(
            ReconciliationTestData.PaymentId, seed.IntentSnapshot, seed.LedgerSnapshot,
            seed.FinalitySnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(0, ledger.SnapshotReads);
    }

    [Fact]
    public async Task LedgerEntryLimit_CannotSilentlyTruncatePaymentHistory()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var first = ReconciliationTestData.Effect(id: 1, amount: 500_000);
        var second = ReconciliationTestData.Effect(id: 2, amount: 750_000, logIndex: 2);
        ReconciliationEvaluation seed = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [first, second],
            [ReconciliationTestData.Qualified(first, 1), ReconciliationTestData.Qualified(second, 2)]);
        var processor = new PaymentReconciliationProcessor(
            new ReconciliationPolicy(ReconciliationTestData.ChainId, ReconciliationTestData.Router,
                "local-reconciliation-v1", maxLedgerEntriesPerPayment: 1),
            new FakeIntentReader(seed.IntentSnapshot),
            new FakeLedgerReader(seed.LedgerSnapshot, [first, second]),
            new FakeFinalityReader(seed.FinalitySnapshot, seed.FinalityTransitions),
            new SqliteReconciliationStore(database),
            new ReconciliationTestData.FixedTimeProvider(ReconciliationTestData.Now));

        await Assert.ThrowsAsync<ReconciliationException>(() => processor.ReconcileAsync(
            ReconciliationTestData.PaymentId, seed.IntentSnapshot, seed.LedgerSnapshot,
            seed.FinalitySnapshot, TestContext.Current.CancellationToken));
        Assert.Empty(await new SqliteReconciliationStore(database).GetReportsAsync(
            ReconciliationTestData.PaymentId, 10, TestContext.Current.CancellationToken));
    }

    private sealed class FakeIntentReader(PaymentIntentReadSnapshot snapshot) : IPaymentIntentReader
    {
        public ValueTask<PaymentIntentReadSnapshot> GetSnapshotAsync(
            PaymentId paymentId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class FakeLedgerReader(
        LedgerReadSnapshot snapshot,
        IReadOnlyList<LedgerEntry> entries) : ILedgerEntryReader
    {
        public int SnapshotReads { get; private set; }
        public ValueTask<LedgerReadSnapshot> GetSnapshotAsync(EvmChainId chainId, EvmAddress router, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); SnapshotReads++; return ValueTask.FromResult(snapshot); }
        public ValueTask<LedgerCheckpoint?> GetCheckpointAsync(EvmChainId chainId, EvmAddress router, CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot.Checkpoint);
        public ValueTask<long> GetEntryHighWatermarkAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot.EntryHighWatermark);
        public ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(EvmChainId chainId, EvmAddress router, long afterEntryId, long throughEntryId, int maxCount, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<LedgerEntry>>(entries.Where(x => x.EntryId > afterEntryId && x.EntryId <= throughEntryId).Take(maxCount).ToArray());
        public ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesByPaymentIdAsync(EvmChainId chainId, EvmAddress router, PaymentId paymentId, long throughEntryId, int maxCount, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<LedgerEntry>>(entries.Where(x => x.PaymentId == paymentId && x.EntryId <= throughEntryId).Take(maxCount).ToArray());
    }

    private sealed class FakeFinalityReader(
        FinalityReadSnapshot snapshot,
        IReadOnlyList<FinalityTransition> transitions) : IFinalityReader
    {
        public ValueTask<FinalityReadSnapshot> GetSnapshotAsync(EvmChainId chainId, EvmAddress router, CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
        public ValueTask<FinalityCheckpoint?> GetCheckpointAsync(EvmChainId chainId, EvmAddress router, CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot.Checkpoint);
        public ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsAsync(EvmChainId chainId, EvmAddress router, long ledgerEffectEntryId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<FinalityTransition>>(transitions.Where(x => x.LedgerEffectEntryId == ledgerEffectEntryId).ToArray());
        public ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsThroughAsync(EvmChainId chainId, EvmAddress router, long ledgerEffectEntryId, long throughTransitionId, int maxCount, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<FinalityTransition>>(transitions.Where(x => x.LedgerEffectEntryId == ledgerEffectEntryId && x.TransitionId <= throughTransitionId).Take(maxCount).ToArray());
    }
}
