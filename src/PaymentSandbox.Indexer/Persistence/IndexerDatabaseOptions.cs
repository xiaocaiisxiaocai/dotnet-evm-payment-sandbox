namespace PaymentSandbox.Indexer.Persistence;

/// <summary>Validated file-system configuration for indexer observations.</summary>
public sealed record IndexerDatabaseOptions
{
    public IndexerDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
