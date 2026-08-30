namespace PaymentSandbox.Authentication.Persistence;

/// <summary>Validated file and capacity configuration for durable SIWE challenges.</summary>
public sealed record SiweChallengeDatabaseOptions
{
    public SiweChallengeDatabaseOptions(
        string databasePath,
        int capacity = 1_024)
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
