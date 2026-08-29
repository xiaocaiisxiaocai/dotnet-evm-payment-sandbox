using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Chain;

/// <summary>The last block durably scanned for one chain and Router stream.</summary>
/// <remarks>
/// A checkpoint is only a restart cursor. It does not mean the block is final,
/// canonical forever, or safe to credit. Later reorg work may move the active
/// cursor backward while retaining the original append-only observations.
/// </remarks>
public sealed record ChainObservationCheckpoint
{
    public ChainObservationCheckpoint(
        EvmChainId chainId,
        EvmAddress router,
        long startBlockNumber,
        long lastBlockNumber,
        EvmHash lastBlockHash,
        long revision,
        DateTimeOffset updatedAtUtc)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startBlockNumber);
        if (lastBlockNumber < startBlockNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastBlockNumber),
                "The checkpoint cannot precede its configured start block.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        StartBlockNumber = startBlockNumber;
        LastBlockNumber = lastBlockNumber;
        LastBlockHash = lastBlockHash ?? throw new ArgumentNullException(nameof(lastBlockHash));
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long StartBlockNumber { get; }

    public long LastBlockNumber { get; }

    public EvmHash LastBlockHash { get; }

    public long Revision { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
