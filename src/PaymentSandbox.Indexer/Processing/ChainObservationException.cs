namespace PaymentSandbox.Indexer.Processing;

/// <summary>Raised when untrusted RPC data cannot satisfy batch invariants.</summary>
public sealed class ChainObservationException : InvalidOperationException
{
    public ChainObservationException(string message)
        : base(message)
    {
    }

    public ChainObservationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
