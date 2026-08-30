using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Lifecycle;

/// <summary>Validated immutable facts used to reserve one operation and nonce.</summary>
public sealed record PreparedPaymentOperation(
    PaymentTransactionRequest Request,
    TransactionLifecyclePolicy Policy,
    string Calldata,
    long ObservedPendingNonce,
    string RequestFingerprint,
    DateTimeOffset CreatedAtUtc);

/// <summary>One signed initial or replacement attempt awaiting atomic persistence.</summary>
public sealed record PreparedTransactionAttempt(
    TransactionOperationId OperationId,
    int ExpectedPreviousAttemptCount,
    TransactionFeeQuote Fee,
    SignedTransactionPayload Payload,
    string UnsignedFingerprint,
    DateTimeOffset CreatedAtUtc);

public enum LifecycleCommitDisposition
{
    Applied,
    Replayed,
    NoWork,
}

public sealed record LifecycleCommitResult(
    LifecycleCommitDisposition Disposition,
    TransactionLifecycleSnapshot Snapshot);

public sealed record BroadcastObservationCommand(
    TransactionOperationId OperationId,
    long AttemptId,
    TransactionHash TransactionHash,
    Abstractions.TransactionBroadcastOutcome Outcome,
    DateTimeOffset ObservedAtUtc);

public sealed record ReceiptObservationCommand(
    TransactionOperationId OperationId,
    long AttemptId,
    TransactionReceiptObservation Receipt,
    DateTimeOffset ObservedAtUtc);
