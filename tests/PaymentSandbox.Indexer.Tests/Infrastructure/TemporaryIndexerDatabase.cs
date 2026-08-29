using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Indexer.Tests.Infrastructure;

/// <summary>Owns one isolated indexer database and path-bounded cleanup.</summary>
internal sealed class TemporaryIndexerDatabase : IAsyncDisposable
{
    private const string DirectoryPrefix = "payment-sandbox-indexer-tests-";
    private readonly string _temporaryRoot;

    public TemporaryIndexerDatabase()
    {
        string systemTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _temporaryRoot = Path.Combine(
            systemTemporaryRoot,
            $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        DatabasePath = Path.Combine(_temporaryRoot, "chain-observations.db");
    }

    public string DatabasePath { get; }

    public ValueTask DisposeAsync()
    {
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
