namespace PaymentSandbox.Orchestrator.Persistence;

public sealed record TransactionLifecycleDatabaseOptions
{
    public TransactionLifecycleDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
