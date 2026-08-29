namespace PaymentSandbox.Finality.Persistence;

public sealed record FinalityDatabaseOptions
{
    public FinalityDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
