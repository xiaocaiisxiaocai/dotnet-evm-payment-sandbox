using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Ledger.Processing;
using PaymentSandbox.Ledger.Tests.Infrastructure;

namespace PaymentSandbox.Ledger.Tests.Integration;

public sealed class IndexerLedgerReorganizationTests
{
    [Fact]
    public async Task RealIndexerReorganization_ProducesReversalAndReplacementEffect()
    {
        await using var temporary = new TemporaryLedgerDatabase();
        string indexerPath = Path.Combine(
            Path.GetDirectoryName(temporary.DatabasePath)!,
            "chain-observations.db");
        var indexerDatabase = new IndexerDatabase(
            new IndexerDatabaseOptions(indexerPath),
            new LedgerTestData.FixedTimeProvider(LedgerTestData.Now));
        LedgerDatabase ledgerDatabase = LedgerTestData.CreateDatabase(temporary.DatabasePath);
        await indexerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        await ledgerDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        var source = new SqliteChainObservationStore(indexerDatabase);
        var ledger = new SqliteLedgerStore(ledgerDatabase);
        var processor = new CanonicalPaymentLedgerProcessor(
            new CanonicalPaymentLedgerPolicy(LedgerTestData.ChainId, LedgerTestData.Router),
            source,
            ledger,
            new LedgerTestData.FixedTimeProvider(LedgerTestData.Now));

        PaymentRecordedObservation oldForkPayment = LedgerTestData.Payment();
        var initialBatch = new ChainObservationBatch(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            startBlockNumber: 100,
            [
                new ObservedBlock(100, LedgerTestData.Hash('1'), LedgerTestData.Hash('0')),
                new ObservedBlock(101, LedgerTestData.Hash('2'), LedgerTestData.Hash('1')),
            ],
            [oldForkPayment],
            LedgerTestData.Now);
        ObservationCommitResult initial = await source.CommitBatchAsync(
            null,
            initialBatch,
            TestContext.Current.CancellationToken);
        long initialHighWatermark = await source.GetCanonicalityHighWatermarkAsync(
            TestContext.Current.CancellationToken);
        await processor.ProcessThroughTransitionAsync(
            initialHighWatermark,
            TestContext.Current.CancellationToken);

        PaymentRecordedObservation replacementPayment = LedgerTestData.Payment(
            blockHash: 'e',
            transactionHash: 'd',
            logIndex: 7,
            amount: 2_500_000);
        var ancestor = new ObservedBlock(
            100,
            LedgerTestData.Hash('1'),
            LedgerTestData.Hash('0'));
        var replacementBatch = new ChainObservationBatch(
            LedgerTestData.ChainId,
            LedgerTestData.Router,
            startBlockNumber: 100,
            [
                new ObservedBlock(101, LedgerTestData.Hash('e'), LedgerTestData.Hash('1')),
                new ObservedBlock(102, LedgerTestData.Hash('f'), LedgerTestData.Hash('e')),
            ],
            [replacementPayment],
            LedgerTestData.Now.AddMinutes(1));
        await source.CommitReorganizationAsync(
            initial.Checkpoint,
            ancestor,
            replacementBatch,
            TestContext.Current.CancellationToken);
        long reorganizedHighWatermark = await source.GetCanonicalityHighWatermarkAsync(
            TestContext.Current.CancellationToken);

        LedgerProcessingResult projected = await processor.ProcessThroughTransitionAsync(
            reorganizedHighWatermark,
            TestContext.Current.CancellationToken);
        IReadOnlyList<LedgerEntry> oldEntries = await GetEntriesAsync(ledger, oldForkPayment);
        IReadOnlyList<LedgerEntry> replacementEntries = await GetEntriesAsync(
            ledger,
            replacementPayment);

        Assert.Equal(2, initialHighWatermark);
        Assert.Equal(5, reorganizedHighWatermark);
        Assert.Equal(3, projected.SourceTransitionCount);
        Assert.Equal(1, projected.CanonicalPaymentCount);
        Assert.Equal(1, projected.ReversalCount);
        Assert.Equal(
            [LedgerEntryKind.CanonicalPayment, LedgerEntryKind.CanonicalPaymentReversal],
            oldEntries.Select(entry => entry.Kind));
        Assert.Equal(oldEntries[0].EntryId, oldEntries[1].ReversesEntryId);
        Assert.Equal(
            LedgerEntryKind.CanonicalPayment,
            Assert.Single(replacementEntries).Kind);

        // Indexer evidence is append-only too: both block occurrences and both
        // payment facts remain queryable after the active branch changes.
        Assert.Single(await source.GetPaymentsAsync(
            oldForkPayment.ChainId,
            oldForkPayment.Router,
            oldForkPayment.BlockNumber,
            oldForkPayment.BlockHash,
            maxCount: 10,
            TestContext.Current.CancellationToken));
        Assert.Single(await source.GetPaymentsAsync(
            replacementPayment.ChainId,
            replacementPayment.Router,
            replacementPayment.BlockNumber,
            replacementPayment.BlockHash,
            maxCount: 10,
            TestContext.Current.CancellationToken));
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
}
