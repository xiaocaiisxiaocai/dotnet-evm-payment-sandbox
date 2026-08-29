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

/// <summary>Atomic append boundary for source copies, decisions, and checkpoint.</summary>
public interface IFinalityStore
{
    ValueTask<FinalityCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default);

    ValueTask<FinalityCommitResult> CommitAsync(
        FinalityCheckpoint? expectedPrevious,
        FinalityEvaluationBatch batch,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long ledgerEffectEntryId,
        CancellationToken cancellationToken = default);
}
