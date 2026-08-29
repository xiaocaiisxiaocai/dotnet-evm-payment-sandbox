using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Api.Tests.Infrastructure;

/// <summary>Owns one isolated SQLite file and its safely bounded cleanup.</summary>
internal sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private const string DirectoryPrefix = "payment-sandbox-api-tests-";
    private readonly string _temporaryRoot;

    public TemporarySqliteDatabase()
    {
        string systemTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _temporaryRoot = Path.Combine(
            systemTemporaryRoot,
            $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        DatabasePath = Path.Combine(_temporaryRoot, "payment-intents.db");
    }

    public string DatabasePath { get; }

    public ValueTask DisposeAsync()
    {
        // Pooled connections can retain Windows file handles after a test host
        // stops. Clear only provider pools, then validate the exact owned path
        // before recursive cleanup.
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
