namespace PaymentSandbox.Contracts.Identity;

public enum PaymentRouterIdentityFailure
{
    RpcRequestFailed,
    UnexpectedChainId,
    CodeMissing,
    CodeMalformed,
    RuntimeCodeHashMismatch,
}

/// <summary>Signals that the configured endpoint did not prove the expected Router identity.</summary>
public sealed class PaymentRouterIdentityException : Exception
{
    internal PaymentRouterIdentityException(
        PaymentRouterIdentityFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public PaymentRouterIdentityFailure Failure { get; }
}
