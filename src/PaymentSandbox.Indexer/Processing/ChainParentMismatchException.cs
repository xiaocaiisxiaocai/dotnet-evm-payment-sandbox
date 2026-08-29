using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Indexer.Processing;

/// <summary>
/// Identifies the precise parent-link failure so the processor can distinguish
/// a reorg at the durable boundary from inconsistent data inside one RPC read.
/// </summary>
public sealed class ChainParentMismatchException : ChainObservationException
{
    public ChainParentMismatchException(
        long blockNumber,
        EvmHash expectedParentHash,
        EvmHash observedParentHash)
        : base(
            $"Block {blockNumber} parent {observedParentHash} does not extend " +
            $"{expectedParentHash}.")
    {
        BlockNumber = blockNumber;
        ExpectedParentHash = expectedParentHash;
        ObservedParentHash = observedParentHash;
    }

    public long BlockNumber { get; }

    public EvmHash ExpectedParentHash { get; }

    public EvmHash ObservedParentHash { get; }
}
