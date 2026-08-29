namespace PaymentSandbox.Ledger.Processing;

/// <summary>Raised when append-only source data cannot satisfy ledger invariants.</summary>
public sealed class LedgerProcessingException : InvalidOperationException
{
    public LedgerProcessingException(string message)
        : base(message)
    {
    }

    public LedgerProcessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
