using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Indexer.Persistence;

/// <summary>Read-only append-log boundary consumed by downstream evidence processors.</summary>
public interface IChainObservationReader
{
    /// <summary>Reads one stream head and the global transition cursor atomically.</summary>
    ValueTask<ChainObservationSnapshot> GetCanonicalSnapshotAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the largest transition ID currently committed by any stream.</summary>
    ValueTask<long> GetCanonicalityHighWatermarkAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one stream's transitions in global append order. IDs belonging to
    /// other streams may create gaps and are intentionally omitted.
    /// </summary>
    ValueTask<IReadOnlyList<BlockCanonicalityTransition>> GetCanonicalityTransitionsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long afterTransitionId,
        long throughTransitionId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>Reads events tied to one exact block occurrence, never just a height.</summary>
    ValueTask<IReadOnlyList<PaymentRecordedObservation>> GetPaymentsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        int maxCount,
        CancellationToken cancellationToken = default);
}
