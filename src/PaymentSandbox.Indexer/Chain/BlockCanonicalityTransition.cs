using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Chain;

public enum BlockCanonicality
{
    Canonical,
    Noncanonical,
}

/// <summary>One append-only change to the local branch selection.</summary>
/// <remarks>
/// Canonicality is local observation state. It is not confirmation, finality,
/// settlement, or permission to move value.
/// </remarks>
public sealed record BlockCanonicalityTransition
{
    public BlockCanonicalityTransition(
        long transitionId,
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        long checkpointRevision,
        BlockCanonicality canonicality,
        string reason,
        DateTimeOffset changedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transitionId);
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointRevision);
        if (!Enum.IsDefined(canonicality))
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicality),
                "The canonicality value is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        TransitionId = transitionId;
        BlockNumber = blockNumber;
        BlockHash = blockHash ?? throw new ArgumentNullException(nameof(blockHash));
        CheckpointRevision = checkpointRevision;
        Canonicality = canonicality;
        Reason = reason;
        ChangedAtUtc = changedAtUtc.ToUniversalTime();
    }

    public long TransitionId { get; }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long BlockNumber { get; }

    public EvmHash BlockHash { get; }

    public long CheckpointRevision { get; }

    public BlockCanonicality Canonicality { get; }

    public string Reason { get; }

    public DateTimeOffset ChangedAtUtc { get; }
}
