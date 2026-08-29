using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;

namespace PaymentSandbox.Finality.Tests.Infrastructure;

internal sealed class FakeObservationReader(ChainObservationSnapshot snapshot)
    : IChainObservationReader
{
    internal ChainObservationSnapshot Snapshot { get; set; } = snapshot;
    internal int SnapshotReads { get; private set; }

    public ValueTask<ChainObservationSnapshot> GetCanonicalSnapshotAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotReads++;
        return ValueTask.FromResult(Snapshot);
    }

    public ValueTask<long> GetCanonicalityHighWatermarkAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<IReadOnlyList<BlockCanonicalityTransition>>
        GetCanonicalityTransitionsAsync(
            EvmChainId chainId,
            EvmAddress router,
            long afterTransitionId,
            long throughTransitionId,
            int maxCount,
            CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<IReadOnlyList<PaymentRecordedObservation>> GetPaymentsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        int maxCount,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeLedgerReader(
    LedgerCheckpoint checkpoint,
    IReadOnlyList<LedgerEntry> entries) : ILedgerEntryReader
{
    internal LedgerCheckpoint Checkpoint { get; set; } = checkpoint;
    internal IReadOnlyList<LedgerEntry> Entries { get; set; } = entries;
    internal long HighWatermark { get; set; } = entries.Count == 0 ? 0 : entries.Max(item => item.EntryId);
    internal int CheckpointReads { get; private set; }
    internal int HighWatermarkReads { get; private set; }
    internal int EntryReads { get; private set; }

    public ValueTask<LedgerCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckpointReads++;
        return ValueTask.FromResult<LedgerCheckpoint?>(Checkpoint);
    }

    public ValueTask<long> GetEntryHighWatermarkAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HighWatermarkReads++;
        return ValueTask.FromResult(HighWatermark);
    }

    public ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        EvmChainId chainId,
        EvmAddress router,
        long afterEntryId,
        long throughEntryId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EntryReads++;
        IReadOnlyList<LedgerEntry> result = Entries
            .Where(item => item.ChainId == chainId && item.Router == router)
            .Where(item => item.EntryId > afterEntryId && item.EntryId <= throughEntryId)
            .OrderBy(item => item.EntryId)
            .Take(maxCount)
            .ToArray();
        return ValueTask.FromResult(result);
    }
}
