using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Chain;

/// <summary>A contiguous range and its decoded logs, committed atomically.</summary>
public sealed record ChainObservationBatch
{
    public ChainObservationBatch(
        EvmChainId chainId,
        EvmAddress router,
        long startBlockNumber,
        IReadOnlyList<ObservedBlock> blocks,
        IReadOnlyList<PaymentRecordedObservation> payments,
        DateTimeOffset observedAtUtc)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        ArgumentOutOfRangeException.ThrowIfNegative(startBlockNumber);
        StartBlockNumber = startBlockNumber;
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(payments);
        if (blocks.Count == 0)
        {
            throw new ArgumentException("An observation batch must contain at least one block.", nameof(blocks));
        }

        // IReadOnlyList only limits this reference; its source may still be a
        // mutable List. Snapshot both collections so validation and SQL commit
        // always see the same batch even when a caller retains its input lists.
        Blocks = blocks.ToArray();
        Payments = payments.ToArray();
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long StartBlockNumber { get; }

    public IReadOnlyList<ObservedBlock> Blocks { get; }

    public IReadOnlyList<PaymentRecordedObservation> Payments { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public ObservedBlock LastBlock => Blocks[^1];
}
