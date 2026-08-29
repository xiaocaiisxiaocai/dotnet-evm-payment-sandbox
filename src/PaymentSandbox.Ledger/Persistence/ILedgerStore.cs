using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Ledger.Persistence;

public enum LedgerCommitDisposition
{
    Applied,
    Replayed,
}

public sealed record LedgerCommitResult(
    LedgerCommitDisposition Disposition,
    LedgerCheckpoint Checkpoint);

/// <summary>Atomic append boundary for provisional effects and their source cursor.</summary>
public interface ILedgerStore
{
    ValueTask<LedgerCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    ValueTask<LedgerCommitResult> CommitAsync(
        LedgerCheckpoint? expectedPrevious,
        CanonicalPaymentBatch batch,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        EvmChainId chainId,
        EvmAddress router,
        EvmHash blockHash,
        EvmHash transactionHash,
        long logIndex,
        CancellationToken cancellationToken = default);
}
