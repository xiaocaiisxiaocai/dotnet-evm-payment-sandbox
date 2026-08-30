using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Reconciliation.Tests.Infrastructure;

internal sealed class TemporaryReconciliationDatabase : IAsyncDisposable
{
    private const string Prefix = "payment-sandbox-reconciliation-tests-";
    private readonly string _directory;

    public TemporaryReconciliationDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        IntentPath = Path.Combine(_directory, "intents.db");
        IndexerPath = Path.Combine(_directory, "indexer.db");
        LedgerPath = Path.Combine(_directory, "ledger.db");
        FinalityPath = Path.Combine(_directory, "finality.db");
        DatabasePath = Path.Combine(_directory, "reconciliation.db");
    }

    public string DatabasePath { get; }
    public string IntentPath { get; }
    public string IndexerPath { get; }
    public string LedgerPath { get; }
    public string FinalityPath { get; }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        string resolved = Path.GetFullPath(_directory);
        if (!Path.GetFileName(resolved).StartsWith(Prefix, StringComparison.Ordinal) ||
            !resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected path {resolved}.");
        }

        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        return ValueTask.CompletedTask;
    }
}
