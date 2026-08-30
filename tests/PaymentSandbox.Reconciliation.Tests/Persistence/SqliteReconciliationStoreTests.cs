using Microsoft.Data.Sqlite;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Persistence;
using PaymentSandbox.Reconciliation.Reports;
using PaymentSandbox.Reconciliation.Tests.Infrastructure;

namespace PaymentSandbox.Reconciliation.Tests.Persistence;

public sealed class SqliteReconciliationStoreTests
{
    [Fact]
    public async Task InitializeAsync_CreatesStrictSchemaIdempotently()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_schema WHERE name = 'reconciliation_reports';";
        string sql = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Contains("STRICT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownOutcomeRetry_VerifiesEvidenceAndReturnsReplayed()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteReconciliationStore(database);
        var effect = ReconciliationTestData.Effect();
        ReconciliationEvaluation first = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);
        ReconciliationEvaluation retry = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)],
            ReconciliationTestData.Now.AddHours(1));

        ReconciliationCommitResult applied = await store.CommitAsync(first, TestContext.Current.CancellationToken);
        ReconciliationCommitResult replayed = await store.CommitAsync(retry, TestContext.Current.CancellationToken);

        Assert.Equal(ReconciliationCommitDisposition.Applied, applied.Disposition);
        Assert.Equal(ReconciliationCommitDisposition.Replayed, replayed.Disposition);
        Assert.Equal(applied.Report.ReportId, replayed.Report.ReportId);
        Assert.Single(await store.GetReportsAsync(ReconciliationTestData.PaymentId, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SameCoordinatesWithChangedFact_FailsClosed()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteReconciliationStore(database);
        var firstEffect = ReconciliationTestData.Effect(amount: 1_250_000);
        var changedEffect = firstEffect with { Amount = new PaymentSandbox.Domain.Payments.RawTokenAmount(9) };
        await store.CommitAsync(ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [firstEffect], [ReconciliationTestData.Qualified(firstEffect)]),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ReconciliationConflictException>(() => store.CommitAsync(
            ReconciliationTestData.Evaluation(
                ReconciliationTestData.Intent(), [changedEffect], [ReconciliationTestData.Qualified(changedEffect)]),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ConcurrentIdenticalWriters_CommitOnceAndReplayOnce()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var effect = ReconciliationTestData.Effect();
        ReconciliationEvaluation value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);

        ReconciliationCommitResult[] results = await Task.WhenAll(
            new SqliteReconciliationStore(database).CommitAsync(value, TestContext.Current.CancellationToken).AsTask(),
            new SqliteReconciliationStore(database).CommitAsync(value, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, results.Count(item => item.Disposition == ReconciliationCommitDisposition.Applied));
        Assert.Equal(1, results.Count(item => item.Disposition == ReconciliationCommitDisposition.Replayed));
    }

    [Fact]
    public async Task NewSourceWatermark_AppendsAnotherReport()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteReconciliationStore(database);
        var first = ReconciliationTestData.Effect(id: 1, amount: 500_000);
        var second = ReconciliationTestData.Effect(id: 2, amount: 750_000, logIndex: 2);
        await store.CommitAsync(ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [first], [ReconciliationTestData.Qualified(first)]),
            TestContext.Current.CancellationToken);
        await store.CommitAsync(ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [first, second],
            [ReconciliationTestData.Qualified(first, 1), ReconciliationTestData.Qualified(second, 2)]),
            TestContext.Current.CancellationToken);

        IReadOnlyList<ReconciliationReport> reports = await store.GetReportsAsync(
            ReconciliationTestData.PaymentId, 10, TestContext.Current.CancellationToken);
        Assert.Equal(2, reports.Count);
        Assert.False(reports[0].IsConsistent);
        Assert.True(reports[1].IsConsistent);
    }

    [Fact]
    public async Task TamperedDurableSummary_IsRejectedDuringReplay()
    {
        await using var temporary = new TemporaryReconciliationDatabase();
        ReconciliationDatabase database = ReconciliationTestData.Database(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteReconciliationStore(database);
        var effect = ReconciliationTestData.Effect();
        ReconciliationEvaluation value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);
        await store.CommitAsync(value, TestContext.Current.CancellationToken);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE reconciliation_reports SET matching_active_amount_raw = '7' WHERE report_id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<ReconciliationConflictException>(() =>
            store.CommitAsync(value, TestContext.Current.CancellationToken).AsTask());
    }
}
