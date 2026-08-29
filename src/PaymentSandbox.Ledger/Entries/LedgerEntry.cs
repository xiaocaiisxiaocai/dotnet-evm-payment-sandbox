using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Ledger.Entries;

public enum LedgerEntryKind
{
    CanonicalPayment,
    CanonicalPaymentReversal,
}

/// <summary>Append-only provisional effect derived from one exact chain occurrence.</summary>
/// <remarks>
/// These entries explain local canonicality changes. They are not a finalized
/// balance, payout authorization, or proof that an unusual token delivered value.
/// </remarks>
public sealed record LedgerEntry(
    long EntryId,
    LedgerEntryKind Kind,
    long SourceTransitionId,
    long SourceCheckpointRevision,
    EvmChainId ChainId,
    EvmAddress Router,
    long BlockNumber,
    EvmHash BlockHash,
    EvmHash TransactionHash,
    long LogIndex,
    PaymentId PaymentId,
    EvmAddress Payer,
    EvmAddress Token,
    EvmAddress Merchant,
    RawTokenAmount Amount,
    long? ReversesEntryId,
    DateTimeOffset SourceChangedAtUtc,
    DateTimeOffset RecordedAtUtc);
