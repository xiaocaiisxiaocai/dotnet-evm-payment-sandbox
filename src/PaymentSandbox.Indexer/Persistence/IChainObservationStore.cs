using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Indexer.Persistence;

public enum ObservationCommitDisposition
{
    Applied,
    Reorganized,
    Replayed,
}

public sealed record ObservationCommitResult(
    ObservationCommitDisposition Disposition,
    ChainObservationCheckpoint Checkpoint);

/// <summary>Atomic persistence boundary for observations and their restart cursor.</summary>
public interface IChainObservationStore
{
    ValueTask<ChainObservationCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the block occurrence currently selected at one height.</summary>
    ValueTask<ObservedBlock?> GetCanonicalBlockAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        CancellationToken cancellationToken = default);

    ValueTask<ObservationCommitResult> CommitBatchAsync(
        ChainObservationCheckpoint? expectedPrevious,
        ChainObservationBatch batch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically detaches the old suffix, attaches its replacement, and moves
    /// the checkpoint. Source observations are retained on both forks.
    /// </summary>
    ValueTask<ObservationCommitResult> CommitReorganizationAsync(
        ChainObservationCheckpoint expectedPrevious,
        ObservedBlock commonAncestor,
        ChainObservationBatch replacement,
        CancellationToken cancellationToken = default);
}
