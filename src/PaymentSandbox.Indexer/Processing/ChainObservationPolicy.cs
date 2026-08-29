using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Processing;

/// <summary>Operator-reviewed identity and range limits for one observation stream.</summary>
public sealed record ChainObservationPolicy
{
    public ChainObservationPolicy(
        EvmChainId chainId,
        EvmAddress router,
        long startBlockNumber,
        int maxBatchSize = 1_000,
        int maxLogsPerBatch = 10_000)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startBlockNumber);
        if (maxBatchSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchSize),
                "The batch size must be between 1 and 10,000 blocks.");
        }

        if (maxLogsPerBatch is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLogsPerBatch),
                "The log limit must be between 1 and 100,000 observations.");
        }

        StartBlockNumber = startBlockNumber;
        MaxBatchSize = maxBatchSize;
        MaxLogsPerBatch = maxLogsPerBatch;
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long StartBlockNumber { get; }

    public int MaxBatchSize { get; }

    public int MaxLogsPerBatch { get; }
}
