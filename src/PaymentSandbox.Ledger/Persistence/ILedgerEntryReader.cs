using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Ledger.Persistence;

/// <summary>Read-only append-log boundary for downstream ledger projections.</summary>
public interface ILedgerEntryReader
{
    ValueTask<LedgerReadSnapshot> GetSnapshotAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    ValueTask<LedgerCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the largest entry ID committed by any ledger stream.</summary>
    ValueTask<long> GetEntryHighWatermarkAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one stream in global entry order. IDs owned by other streams may
    /// create gaps and are intentionally omitted.
    /// </summary>
    ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        EvmChainId chainId,
        EvmAddress router,
        long afterEntryId,
        long throughEntryId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>Reads all selected-stream entries for one payment ID through a fixed watermark.</summary>
    ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesByPaymentIdAsync(
        EvmChainId chainId,
        EvmAddress router,
        PaymentId paymentId,
        long throughEntryId,
        int maxCount,
        CancellationToken cancellationToken = default);
}
