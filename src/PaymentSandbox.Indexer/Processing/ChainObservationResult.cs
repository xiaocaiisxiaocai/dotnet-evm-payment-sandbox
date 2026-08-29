using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;

namespace PaymentSandbox.Indexer.Processing;

public enum ChainObservationDisposition
{
    Applied,
    Reorganized,
    Replayed,
    NoWork,
}

/// <summary>The bounded outcome of one explicit scan request.</summary>
public sealed record ChainObservationResult(
    ChainObservationDisposition Disposition,
    ChainObservationCheckpoint? Checkpoint,
    int ObservedBlockCount,
    int ObservedPaymentCount,
    int DetachedBlockCount = 0)
{
    internal static ChainObservationDisposition FromStore(ObservationCommitDisposition disposition) =>
        disposition switch
        {
            ObservationCommitDisposition.Applied => ChainObservationDisposition.Applied,
            ObservationCommitDisposition.Reorganized => ChainObservationDisposition.Reorganized,
            _ => ChainObservationDisposition.Replayed,
        };
}
