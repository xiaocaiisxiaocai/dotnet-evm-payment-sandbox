using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Finality.Tests.Infrastructure;

/// <summary>Owns three isolated databases with path-bounded cleanup.</summary>
internal sealed class TemporaryFinalityDatabases : IAsyncDisposable
{
    private const string DirectoryPrefix = "payment-sandbox-finality-tests-";
    private readonly string _temporaryRoot;

    public TemporaryFinalityDatabases()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _temporaryRoot = Path.Combine(
            temporaryRoot,
            $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        IndexerPath = Path.Combine(_temporaryRoot, "indexer.db");
        LedgerPath = Path.Combine(_temporaryRoot, "ledger.db");
        FinalityPath = Path.Combine(_temporaryRoot, "finality.db");
    }

    public string IndexerPath { get; }
    public string LedgerPath { get; }
    public string FinalityPath { get; }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string resolvedRoot = Path.GetFullPath(_temporaryRoot);
        bool isOwned = resolvedRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolvedRoot).StartsWith(DirectoryPrefix, StringComparison.Ordinal);
        if (!isOwned)
        {
            throw new InvalidOperationException(
                $"Refusing to delete an unexpected test directory: {resolvedRoot}");
        }

        if (Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
