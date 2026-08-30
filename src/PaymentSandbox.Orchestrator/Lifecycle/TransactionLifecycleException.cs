namespace PaymentSandbox.Orchestrator.Lifecycle;

public sealed class TransactionLifecycleException : Exception
{
    public TransactionLifecycleException(string message) : base(message) { }
    public TransactionLifecycleException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class TransactionLifecycleConflictException(string message) : Exception(message);
