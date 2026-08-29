using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Finality.Transitions;

public enum FinalityTransitionKind
{
    ConfirmationQualified,
    ConfirmationRevoked,
}

public enum FinalityTransitionReason
{
    ConfirmationThresholdReached,
    LedgerEffectReversed,
    ConfirmationThresholdLost,
}

/// <summary>One append-only local confirmation-policy decision.</summary>
public sealed record FinalityTransition(
    long TransitionId,
    long FinalityRevision,
    FinalityTransitionKind Kind,
    long LedgerEffectEntryId,
    long? RevokesTransitionId,
    EvmChainId ChainId,
    EvmAddress Router,
    long HeadBlockNumber,
    EvmHash HeadBlockHash,
    long HeadCheckpointRevision,
    long ConfirmationCount,
    long RequiredConfirmationCount,
    FinalityTransitionReason Reason,
    DateTimeOffset RecordedAtUtc);
