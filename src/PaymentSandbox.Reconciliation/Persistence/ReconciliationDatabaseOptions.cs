namespace PaymentSandbox.Reconciliation.Persistence;

public sealed record ReconciliationDatabaseOptions
{
    public ReconciliationDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
