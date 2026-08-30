using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Persistence;

public interface ITransactionLifecycleStore
{
    ValueTask<LifecycleCommitResult> ReserveAsync(
        PreparedPaymentOperation operation,
        CancellationToken cancellationToken = default);

    ValueTask<TransactionLifecycleSnapshot?> GetAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TransactionAttemptSummary>> GetAttemptsAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default);

    ValueTask<TransactionAttemptPayload?> GetCurrentPayloadAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TransactionAttemptPayload>> GetPayloadsAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default);

    ValueTask<LifecycleCommitResult> CommitAttemptAsync(
        PreparedTransactionAttempt attempt,
        CancellationToken cancellationToken = default);

    ValueTask<LifecycleCommitResult> AppendBroadcastAsync(
        BroadcastObservationCommand observation,
        CancellationToken cancellationToken = default);

    ValueTask<LifecycleCommitResult> AppendReceiptAsync(
        ReceiptObservationCommand observation,
        CancellationToken cancellationToken = default);
}
