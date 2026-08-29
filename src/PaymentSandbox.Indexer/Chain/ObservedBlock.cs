namespace PaymentSandbox.Indexer.Chain;

/// <summary>An exact block identity returned by one RPC observation.</summary>
public sealed record ObservedBlock
{
    public ObservedBlock(long number, EvmHash hash, EvmHash parentHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        Number = number;
        Hash = hash ?? throw new ArgumentNullException(nameof(hash));
        ParentHash = parentHash ?? throw new ArgumentNullException(nameof(parentHash));
    }

    public long Number { get; }

    public EvmHash Hash { get; }

    public EvmHash ParentHash { get; }
}
