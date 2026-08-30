using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Reconciliation.Reports;

public enum ReconciliationDiscrepancyCode
{
    IntentMissing,
    ActivePaymentMissing,
    ReversedPaymentHistory,
    ChainMismatch,
    TokenMismatch,
    MerchantMismatch,
    AmountUnderpaid,
    AmountOverpaid,
    QualificationIncomplete,
}

/// <summary>An immutable explanation of one payment ID at exact source watermarks.</summary>
/// <remarks>
/// <see cref="IsConsistent"/> is local evidence agreement only. It is not token
/// delivery proof, protocol finality, settlement, a balance, or payout authority.
/// </remarks>
public sealed record ReconciliationReport(
    long ReportId,
    PaymentId PaymentId,
    EvmChainId ChainId,
    EvmAddress Router,
    string PolicyId,
    string PolicyFingerprint,
    long IntentPublicationHighWatermark,
    long? IntentPublicationId,
    long LedgerEntryHighWatermark,
    long LedgerCheckpointRevision,
    long FinalityTransitionHighWatermark,
    long FinalityRevision,
    bool IsConsistent,
    int CanonicalOccurrenceCount,
    int ActiveOccurrenceCount,
    int MatchingActiveOccurrenceCount,
    int QualifiedMatchingOccurrenceCount,
    BigInteger MatchingActiveAmount,
    BigInteger QualifiedMatchingAmount,
    IReadOnlyList<ReconciliationDiscrepancyCode> Discrepancies,
    string BatchFingerprint,
    DateTimeOffset EvaluatedAtUtc);
