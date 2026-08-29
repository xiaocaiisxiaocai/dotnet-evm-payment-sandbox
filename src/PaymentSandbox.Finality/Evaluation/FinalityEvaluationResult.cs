using PaymentSandbox.Finality.Persistence;

namespace PaymentSandbox.Finality.Evaluation;

public enum FinalityEvaluationDisposition
{
    Applied,
    Replayed,
    NoWork,
}

public sealed record FinalityEvaluationResult(
    FinalityEvaluationDisposition Disposition,
    FinalityCheckpoint? Checkpoint,
    int SourceLedgerEntryCount,
    int QualificationCount,
    int RevocationCount);
