using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Transitions;

namespace PaymentSandbox.Finality.Persistence;

public enum FinalityCommitDisposition
{
    Applied,
    Replayed,
}

public sealed record FinalityCommitResult(
    FinalityCommitDisposition Disposition,
    FinalityCheckpoint Checkpoint,
    int QualificationCount,
    int RevocationCount);

/// <summary>Read-only Finality boundary for downstream projections.</summary>
public interface IFinalityReader
{
    ValueTask<FinalityReadSnapshot> GetSnapshotAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    ValueTask<FinalityCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long ledgerEffectEntryId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsThroughAsync(
        EvmChainId chainId,
        EvmAddress router,
        long ledgerEffectEntryId,
        long throughTransitionId,
        int maxCount,
        CancellationToken cancellationToken = default);
}

/// <summary>Atomic append boundary for source copies, decisions, and checkpoint.</summary>
public interface IFinalityStore : IFinalityReader
{
    ValueTask<FinalityCommitResult> CommitAsync(
        FinalityCheckpoint? expectedPrevious,
        FinalityEvaluationBatch batch,
        CancellationToken cancellationToken = default);
}
