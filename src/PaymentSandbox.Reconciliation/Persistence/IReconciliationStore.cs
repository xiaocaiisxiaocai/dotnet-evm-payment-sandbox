using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Reports;

namespace PaymentSandbox.Reconciliation.Persistence;

public enum ReconciliationCommitDisposition { Applied, Replayed }

public sealed record ReconciliationCommitResult(
    ReconciliationCommitDisposition Disposition,
    ReconciliationReport Report);

public interface IReconciliationStore
{
    ValueTask<ReconciliationCommitResult> CommitAsync(
        ReconciliationEvaluation evaluation,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ReconciliationReport>> GetReportsAsync(
        PaymentId paymentId,
        int maxCount,
        CancellationToken cancellationToken = default);
}
