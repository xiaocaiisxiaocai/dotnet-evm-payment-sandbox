using PaymentSandbox.Orchestrator.Persistence;

namespace PaymentSandbox.Orchestrator.Tests.Infrastructure;

internal sealed class TemporaryTransactionLifecycleDatabase : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"payment-sandbox-orchestrator-tests-{Guid.NewGuid():N}");

    internal string DatabasePath => Path.Combine(_directory, "transaction-lifecycle.db");

    internal TransactionLifecycleDatabase Create(TimeProvider? timeProvider = null) =>
        new(new TransactionLifecycleDatabaseOptions(DatabasePath),
            timeProvider ?? OrchestratorTestData.TimeProvider);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
