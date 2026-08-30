namespace PaymentSandbox.Permits.Persistence;

/// <summary>Absolute local database location and bounded retained-operation count.</summary>
public sealed record PermitWorkflowDatabaseOptions
{
    public PermitWorkflowDatabaseOptions(string databasePath, int capacity = 1_024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        Capacity = capacity;
    }

    public string DatabasePath { get; }
    public int Capacity { get; }
}
