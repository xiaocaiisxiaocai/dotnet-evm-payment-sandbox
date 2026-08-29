using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Ledger.Tests.Infrastructure;

/// <summary>Owns one isolated ledger database and refuses broad cleanup paths.</summary>
internal sealed class TemporaryLedgerDatabase : IAsyncDisposable
{
    private const string DirectoryPrefix = "payment-sandbox-ledger-tests-";
    private readonly string _temporaryRoot;

    public TemporaryLedgerDatabase()
    {
        string systemTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _temporaryRoot = Path.Combine(
            systemTemporaryRoot,
            $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        DatabasePath = Path.Combine(_temporaryRoot, "canonical-payment-ledger.db");
    }

    public string DatabasePath { get; }

    public ValueTask DisposeAsync()
    {
        // SQLite pooling may retain a Windows file handle after a test connection
        // is disposed. Clearing pools makes the path-bounded cleanup deterministic.
        SqliteConnection.ClearAllPools();
        string systemTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string resolvedRoot = Path.GetFullPath(_temporaryRoot);
        string directoryName = Path.GetFileName(resolvedRoot);
        bool isOwnedPath = resolvedRoot.StartsWith(
                systemTemporaryRoot,
                StringComparison.OrdinalIgnoreCase) &&
            directoryName.StartsWith(DirectoryPrefix, StringComparison.Ordinal);
        if (!isOwnedPath)
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
