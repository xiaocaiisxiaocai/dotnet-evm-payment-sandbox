namespace PaymentSandbox.Api.Persistence;

/// <summary>Validated file-system configuration for the local SQLite database.</summary>
public sealed record PaymentIntentDatabaseOptions
{
    public PaymentIntentDatabaseOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // Resolve the path once at the composition boundary. Store operations
        // must not change destination when the process working directory moves.
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }
}
