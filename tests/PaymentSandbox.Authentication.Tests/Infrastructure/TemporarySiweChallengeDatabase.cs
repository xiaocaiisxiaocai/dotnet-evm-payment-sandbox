using Microsoft.Data.Sqlite;

namespace PaymentSandbox.Authentication.Tests.Infrastructure;

/// <summary>Owns one isolated SIWE database and refuses broad cleanup paths.</summary>
internal sealed class TemporarySiweChallengeDatabase : IAsyncDisposable
{
    private const string DirectoryPrefix = "payment-sandbox-siwe-tests-";
    private readonly string _temporaryRoot;

    internal TemporarySiweChallengeDatabase()
    {
        string systemTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _temporaryRoot = Path.Combine(
            systemTemporaryRoot,
            $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        DatabasePath = Path.Combine(_temporaryRoot, "siwe-challenges.db");
    }

    internal string DatabasePath { get; }

    public ValueTask DisposeAsync()
    {
        // Connection pooling can retain a Windows file handle after disposal.
        // Clearing pools makes deletion deterministic, while the prefix and
        // temp-root checks keep cleanup constrained to this test's directory.
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
                $"Refusing to delete an unexpected SIWE test directory: {resolvedRoot}");
        }

        if (Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
