using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;

namespace PaymentSandbox.Indexer.Processing;

public enum ChainObservationDisposition
{
    Applied,
    Replayed,
    NoWork,
}

/// <summary>The bounded outcome of one explicit scan request.</summary>
public sealed record ChainObservationResult(
    ChainObservationDisposition Disposition,
    ChainObservationCheckpoint? Checkpoint,
    int ObservedBlockCount,
    int ObservedPaymentCount)
{
    internal static ChainObservationDisposition FromStore(ObservationCommitDisposition disposition) =>
        disposition == ObservationCommitDisposition.Applied
            ? ChainObservationDisposition.Applied
            : ChainObservationDisposition.Replayed;
}
