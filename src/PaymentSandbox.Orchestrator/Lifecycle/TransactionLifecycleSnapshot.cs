using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Lifecycle;

public enum TransactionLifecycleState
{
    Reserved,
    Signed,
    BroadcastUnknown,
    Submitted,
    Rejected,
    MinedSucceeded,
    MinedReverted,
}

/// <summary>Non-sensitive summary derived from append-only transaction history.</summary>
public sealed record TransactionLifecycleSnapshot(
    TransactionOperationId OperationId,
    PaymentId PaymentId,
    EvmChainId ChainId,
    EvmAddress Signer,
    EvmAddress Router,
    EvmAddress Token,
    EvmAddress Merchant,
    RawTokenAmount Amount,
    long Nonce,
    long GasLimit,
    string PolicyId,
    string PolicyFingerprint,
    TransactionLifecycleState State,
    int AttemptCount,
    int BroadcastObservationCount,
    TransactionHash? CurrentTransactionHash,
    TransactionHash? MinedTransactionHash,
    long? MinedBlockNumber,
    DateTimeOffset CreatedAtUtc);

/// <summary>Non-sensitive attempt metadata; signed raw bytes are deliberately absent.</summary>
public sealed record TransactionAttemptSummary(
    long AttemptId,
    int Sequence,
    long Nonce,
    TransactionFeeQuote Fee,
    TransactionHash TransactionHash,
    int SignedByteLength,
    int BroadcastObservationCount,
    TransactionBroadcastOutcomeKind? LatestBroadcastOutcome,
    DateTimeOffset CreatedAtUtc);

/// <summary>Sensitive broadcast material loaded only at the broadcaster boundary.</summary>
public sealed record TransactionAttemptPayload(
    TransactionAttemptSummary Summary,
    SignedTransactionPayload Payload,
    string UnsignedFingerprint);
