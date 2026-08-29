using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;

namespace PaymentSandbox.Ledger.Processing;

public enum LedgerProcessingDisposition
{
    Applied,
    Replayed,
    NoWork,
}

/// <summary>Outcome of processing through one explicit source high-watermark.</summary>
public sealed record LedgerProcessingResult(
    LedgerProcessingDisposition Disposition,
    LedgerCheckpoint? Checkpoint,
    int SourceTransitionCount,
    int CanonicalPaymentCount,
    int ReversalCount)
{
    internal static LedgerProcessingDisposition FromStore(LedgerCommitDisposition disposition) =>
        disposition == LedgerCommitDisposition.Applied
            ? LedgerProcessingDisposition.Applied
            : LedgerProcessingDisposition.Replayed;
}
