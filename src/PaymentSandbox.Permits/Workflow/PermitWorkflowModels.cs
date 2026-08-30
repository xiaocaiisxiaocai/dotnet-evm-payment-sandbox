using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Erc2612;
using PaymentSandbox.Permits.Preflight;

namespace PaymentSandbox.Permits.Workflow;

/// <summary>The latest fact in one append-only permit operation history.</summary>
public enum PermitWorkflowState
{
    /// <summary>The observed token nonce is durably reserved; nothing was signed yet.</summary>
    Reserved,

    /// <summary>One verified signature was encoded and its exact calldata is immutable.</summary>
    Prepared,

    /// <summary>Calldata may have escaped, but its transport result is not known.</summary>
    SubmissionUnknown,

    /// <summary>The caller reported transport acceptance; this is not receipt evidence.</summary>
    SubmissionAccepted,

    /// <summary>The caller reported a definite transport rejection.</summary>
    SubmissionRejected,

    /// <summary>The token nonce differs; the workflow cannot attribute who changed it.</summary>
    NonceChanged,

    /// <summary>The exclusive permit deadline has been reached.</summary>
    Expired,
}

/// <summary>Explains whether a durable command created, replayed, or changed state.</summary>
public enum PermitWorkflowCommitDisposition
{
    Created,
    Replayed,
    Applied,
    NoWork,
}

public enum PermitSubmissionOutcome
{
    /// <summary>The transport accepted the payload; mining remains unproven.</summary>
    Accepted,

    /// <summary>The transport definitely rejected the payload.</summary>
    Rejected,

    /// <summary>No new write is needed because unknown was stored before release.</summary>
    Unknown,
}

/// <summary>Immutable payment facts plus the signature-bearing retry bytes.</summary>
public sealed record PermitPaymentPreparation(
    PaymentId PaymentId,
    EvmAddress Merchant,
    EvmAddress RequiredSender,
    string Calldata,
    string CalldataHash,
    DateTimeOffset PreparedAtUtc)
{
    public override string ToString() =>
        $"Prepared permit payment {PaymentId.Value} for {Merchant.Value} " +
        "(signature-bearing calldata redacted)";
}

/// <summary>A redacted projection of immutable facts and the latest transition.</summary>
public sealed record PermitWorkflowSnapshot(
    PermitOperationId OperationId,
    Erc2612PermitDraft Draft,
    VerifiedErc2612TokenSnapshot InitialObservation,
    PermitWorkflowState State,
    long LatestTransitionId,
    int SubmissionAuthorizationCount,
    PermitPaymentPreparation? Preparation)
{
    public override string ToString() =>
        $"Permit workflow {OperationId.Value} is {State} for {Draft.Owner.Value} " +
        "(typed data, signature, and calldata omitted)";
}

/// <summary>One durable command result; callers can distinguish replay from mutation.</summary>
public sealed record PermitWorkflowCommitResult(
    PermitWorkflowCommitDisposition Disposition,
    PermitWorkflowSnapshot Snapshot);

/// <summary>Payload released only after an unknown marker is durable.</summary>
public sealed record PermitSubmissionAuthorization(
    PermitOperationId OperationId,
    long AuthorizationTransitionId,
    EvmAddress RequiredSender,
    string Calldata,
    int AuthorizationSequence,
    VerifiedErc2612TokenSnapshot PreSubmissionObservation)
{
    public override string ToString() =>
        $"Permit submission authorization {AuthorizationSequence} for " +
        $"{OperationId.Value} by {RequiredSender.Value} (calldata redacted)";
}

internal sealed record PermitReservationCommand(
    PermitOperationId OperationId,
    Erc2612PermitDraft Draft,
    VerifiedErc2612TokenSnapshot Observation,
    DateTimeOffset CreatedAtUtc);

internal sealed record PermitPreparationCommand(
    PermitOperationId OperationId,
    long ExpectedTransitionId,
    PreparedErc2612Payment Payment,
    DateTimeOffset PreparedAtUtc);
