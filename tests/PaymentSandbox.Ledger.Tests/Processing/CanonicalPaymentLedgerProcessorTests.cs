using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Ledger.Processing;
using PaymentSandbox.Ledger.Tests.Infrastructure;

namespace PaymentSandbox.Ledger.Tests.Processing;

public sealed class CanonicalPaymentLedgerProcessorTests
{
    [Fact]
    public async Task ProcessThroughTransition_ProjectsCanonicalPayment()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore store, CanonicalPaymentLedgerProcessor processor, _) =
            await CreateProcessorAsync(
                temporary,
                transitions: [LedgerTestData.Transition(4)],
                payments: PaymentMap(LedgerTestData.Payment()),
                highWatermark: 4);

        LedgerProcessingResult result = await processor.ProcessThroughTransitionAsync(
            4,
            TestContext.Current.CancellationToken);
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        IReadOnlyList<LedgerEntry> entries = await store.GetEntriesAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockHash,
            payment.TransactionHash,
            payment.LogIndex,
            TestContext.Current.CancellationToken);

        Assert.Equal(LedgerProcessingDisposition.Applied, result.Disposition);
        Assert.Equal(1, result.SourceTransitionCount);
        Assert.Equal(1, result.CanonicalPaymentCount);
        Assert.Equal(0, result.ReversalCount);
        Assert.Equal(4, result.Checkpoint!.LastSourceTransitionId);
        Assert.Equal(LedgerEntryKind.CanonicalPayment, Assert.Single(entries).Kind);
    }

    [Fact]
    public async Task ProcessAfterReorganization_AppendsReversal()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        var source = new FakeChainObservationReader(
            1,
            [
                LedgerTestData.Transition(1),
                LedgerTestData.Transition(
                    2,
                    BlockCanonicality.Noncanonical,
                    checkpointRevision: 2),
            ],
            PaymentMap(payment));
        (SqliteLedgerStore store, CanonicalPaymentLedgerProcessor processor) =
            await CreateProcessorAsync(temporary, source);
        await processor.ProcessThroughTransitionAsync(1, TestContext.Current.CancellationToken);
        source.HighWatermark = 2;

        LedgerProcessingResult result = await processor.ProcessThroughTransitionAsync(
            2,
            TestContext.Current.CancellationToken);
        IReadOnlyList<LedgerEntry> entries = await store.GetEntriesAsync(
            payment.ChainId,
            payment.Router,
            payment.BlockHash,
            payment.TransactionHash,
            payment.LogIndex,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ReversalCount);
        Assert.Equal(LedgerEntryKind.CanonicalPaymentReversal, entries[1].Kind);
        Assert.Equal(entries[0].EntryId, entries[1].ReversesEntryId);
    }

    [Fact]
    public async Task AlreadyProcessedTarget_ReturnsNoWorkWithoutReadingSource()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        PaymentRecordedObservation payment = LedgerTestData.Payment();
        (SqliteLedgerStore _, CanonicalPaymentLedgerProcessor processor,
            FakeChainObservationReader source) = await CreateProcessorAsync(
                temporary,
                transitions: [LedgerTestData.Transition(1)],
                payments: PaymentMap(payment),
                highWatermark: 1);
        await processor.ProcessThroughTransitionAsync(1, TestContext.Current.CancellationToken);
        int highWatermarkReads = source.HighWatermarkReads;
        int transitionReads = source.TransitionReads;
        int paymentReads = source.PaymentReads;

        LedgerProcessingResult result = await processor.ProcessThroughTransitionAsync(
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(LedgerProcessingDisposition.NoWork, result.Disposition);
        Assert.Equal(highWatermarkReads, source.HighWatermarkReads);
        Assert.Equal(transitionReads, source.TransitionReads);
        Assert.Equal(paymentReads, source.PaymentReads);
    }

    [Fact]
    public async Task TargetBeyondCommittedSource_IsRejectedBeforeTransitionRead()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (_, CanonicalPaymentLedgerProcessor processor, FakeChainObservationReader source) =
            await CreateProcessorAsync(temporary, highWatermark: 5);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            processor.ProcessThroughTransitionAsync(
                6,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, source.HighWatermarkReads);
        Assert.Equal(0, source.TransitionReads);
        Assert.Equal(0, source.PaymentReads);
    }

    [Fact]
    public async Task TransitionLimit_IsCheckedUsingOneLookaheadRow()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (_, CanonicalPaymentLedgerProcessor processor, _) = await CreateProcessorAsync(
            temporary,
            transitions: [LedgerTestData.Transition(1), LedgerTestData.Transition(2)],
            highWatermark: 2,
            maxTransitions: 1);

        LedgerProcessingException exception = await Assert.ThrowsAsync<LedgerProcessingException>(
            () => processor.ProcessThroughTransitionAsync(
                2,
                TestContext.Current.CancellationToken));

        Assert.Contains("more than 1 transitions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaymentLimit_IsCheckedBeforeAnyLedgerCommit()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        PaymentRecordedObservation first = LedgerTestData.Payment(logIndex: 3);
        PaymentRecordedObservation second = LedgerTestData.Payment(logIndex: 4);
        (SqliteLedgerStore store, CanonicalPaymentLedgerProcessor processor, _) =
            await CreateProcessorAsync(
                temporary,
                transitions: [LedgerTestData.Transition(1)],
                payments: PaymentMap(first, second),
                highWatermark: 1,
                maxPayments: 1);

        await Assert.ThrowsAsync<LedgerProcessingException>(() =>
            processor.ProcessThroughTransitionAsync(
                1,
                TestContext.Current.CancellationToken));

        Assert.Null(await store.GetCheckpointAsync(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyStreamInterval_StillRecordsExplicitHighWatermark()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (SqliteLedgerStore _, CanonicalPaymentLedgerProcessor processor, _) =
            await CreateProcessorAsync(temporary, highWatermark: 9);

        LedgerProcessingResult result = await processor.ProcessThroughTransitionAsync(
            9,
            TestContext.Current.CancellationToken);

        // Transition IDs are global to the Indexer database. An empty result can
        // mean IDs 1..9 belonged to other chain/router streams, so cursor 9 is valid.
        Assert.Equal(LedgerProcessingDisposition.Applied, result.Disposition);
        Assert.Equal(9, result.Checkpoint!.LastSourceTransitionId);
        Assert.Equal(0, result.SourceTransitionCount);
    }

    [Fact]
    public async Task SourceFailure_IsWrappedWithBoundaryContext()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        (_, CanonicalPaymentLedgerProcessor processor, FakeChainObservationReader source) =
            await CreateProcessorAsync(temporary, highWatermark: 1);
        source.HighWatermarkException = new IOException("source unavailable");

        LedgerProcessingException exception = await Assert.ThrowsAsync<LedgerProcessingException>(
            () => processor.ProcessThroughTransitionAsync(
                1,
                TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("high-watermark", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<(
        SqliteLedgerStore Store,
        CanonicalPaymentLedgerProcessor Processor,
        FakeChainObservationReader Source)> CreateProcessorAsync(
            TemporaryLedgerDatabase temporary,
            IReadOnlyList<BlockCanonicalityTransition>? transitions = null,
            IReadOnlyDictionary<(long, string), IReadOnlyList<PaymentRecordedObservation>>?
                payments = null,
            long highWatermark = 0,
            int maxTransitions = 1_000,
            int maxPayments = 10_000)
    {
        var source = new FakeChainObservationReader(highWatermark, transitions, payments);
        (SqliteLedgerStore store, CanonicalPaymentLedgerProcessor processor) =
            await CreateProcessorAsync(temporary, source, maxTransitions, maxPayments);
        return (store, processor, source);
    }

    private static async Task<(SqliteLedgerStore Store,
        CanonicalPaymentLedgerProcessor Processor)> CreateProcessorAsync(
            TemporaryLedgerDatabase temporary,
            FakeChainObservationReader source,
            int maxTransitions = 1_000,
            int maxPayments = 10_000)
    {
        LedgerDatabase database = LedgerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteLedgerStore(database);
        var policy = new CanonicalPaymentLedgerPolicy(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            maxTransitions,
            maxPayments);
        var processor = new CanonicalPaymentLedgerProcessor(
            policy,
            source,
            store,
            new LedgerTestData.FixedTimeProvider(LedgerTestData.Now));
        return (store, processor);
    }

    private static IReadOnlyDictionary<(long, string),
        IReadOnlyList<PaymentRecordedObservation>> PaymentMap(
            params PaymentRecordedObservation[] payments) =>
        payments
            .GroupBy(payment => (payment.BlockNumber, payment.BlockHash.Value))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PaymentRecordedObservation>)group.ToArray());
}
