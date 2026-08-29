using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Indexer.Persistence;

public enum ObservationCommitDisposition
{
    Applied,
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

    ValueTask<ObservationCommitResult> CommitBatchAsync(
        ChainObservationCheckpoint? expectedPrevious,
        ChainObservationBatch batch,
        CancellationToken cancellationToken = default);
}
