namespace PaymentSandbox.Ledger.Persistence;

/// <summary>Validated file-system configuration for the provisional ledger.</summary>
public sealed record LedgerDatabaseOptions
{
    public LedgerDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
