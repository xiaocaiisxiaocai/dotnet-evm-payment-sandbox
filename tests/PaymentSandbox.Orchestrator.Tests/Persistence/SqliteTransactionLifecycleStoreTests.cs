using Microsoft.Data.Sqlite;
using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Tests.Infrastructure;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Tests.Persistence;

public sealed class SqliteTransactionLifecycleStoreTests
{
    [Fact]
    public async Task Migration_IsStrictIdempotentAndCreatesAppendOnlyTables()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var database = temporary.Create();
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name IN (
                'transaction_operations', 'transaction_attempts',
                'transaction_broadcast_observations', 'transaction_receipt_observations')
              AND sql LIKE '%STRICT%';
            """;
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task DifferentOperations_ReserveMonotonicLocalNoncesAbovePending()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);

        LifecycleCommitResult first = await components.Processor.CreateAsync(OrchestratorTestData.Request("operation-1"), TestContext.Current.CancellationToken);
        LifecycleCommitResult second = await components.Processor.CreateAsync(OrchestratorTestData.Request("operation-2"), TestContext.Current.CancellationToken);

        Assert.Equal(7, first.Snapshot.Nonce);
        Assert.Equal(8, second.Snapshot.Nonce);
    }

    [Fact]
    public async Task SameOperationIdWithChangedFacts_ConflictsWithoutAnotherNonceRead()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TransactionLifecycleConflictException>(() =>
            components.Processor.CreateAsync(OrchestratorTestData.Request(maxFee: 200), TestContext.Current.CancellationToken));
        Assert.Equal(1, components.Nonces.Calls);
    }

    [Fact]
    public async Task ConcurrentDifferentOperations_GetDistinctNonces()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);

        LifecycleCommitResult[] results = await Task.WhenAll(
            components.Processor.CreateAsync(
                OrchestratorTestData.Request("operation-a"), TestContext.Current.CancellationToken),
            components.Processor.CreateAsync(
                OrchestratorTestData.Request("operation-b"), TestContext.Current.CancellationToken));

        Assert.Equal([7L, 8L], results.Select(item => item.Snapshot.Nonce).Order().ToArray());
    }

    [Fact]
    public async Task NonceLeadLimit_FailsBeforeAnotherOperationIsInserted()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(
            temporary, OrchestratorTestData.Policy(maxNonceLead: 0));
        await components.Processor.CreateAsync(OrchestratorTestData.Request("operation-1"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TransactionLifecycleConflictException>(() =>
            components.Processor.CreateAsync(OrchestratorTestData.Request("operation-2"), TestContext.Current.CancellationToken));
        Assert.Null(await components.Store.GetAsync(TransactionOperationId.Parse("operation-2"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TamperedRawTransaction_IsDetectedBeforeBroadcastMaterialLeavesStore()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={temporary.DatabasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE transaction_attempts SET raw_transaction = '0x0102' WHERE operation_id = $operation;";
            command.Parameters.AddWithValue("$operation", request.OperationId.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<TransactionLifecycleConflictException>(async () =>
            await components.Store.GetCurrentPayloadAsync(
                request.OperationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TamperedUnsignedFingerprint_IsRecomputedBeforePayloadLeavesStore()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={temporary.DatabasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE transaction_attempts SET unsigned_fingerprint = $changed WHERE operation_id = $operation;";
            command.Parameters.AddWithValue("$changed", new string('a', 64));
            command.Parameters.AddWithValue("$operation", request.OperationId.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<TransactionLifecycleConflictException>(async () =>
            await components.Store.GetCurrentPayloadAsync(
                request.OperationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TamperedOperationFacts_AreDetectedOnAnOrdinarySnapshotRead()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={temporary.DatabasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE transaction_operations SET amount_raw = '2' WHERE operation_id = $operation;";
            command.Parameters.AddWithValue("$operation", request.OperationId.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<TransactionLifecycleConflictException>(async () =>
            await components.Store.GetAsync(
                request.OperationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseTrigger_RejectsReceiptBeforeAnyBroadcastObservation()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        TransactionAttemptSummary attempt = Assert.Single(await components.Store.GetAttemptsAsync(
            request.OperationId, TestContext.Current.CancellationToken));
        await using SqliteConnection connection = await temporary.Create().OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO transaction_receipt_observations (
                operation_id, attempt_id, transaction_hash, execution_status,
                block_number, block_hash, gas_used, effective_gas_price_wei,
                observed_at_utc)
            VALUES ($operation, $attempt, $hash, 'succeeded', 12, $block,
                80000, '50', $time);
            """;
        command.Parameters.AddWithValue("$operation", request.OperationId.Value);
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId);
        command.Parameters.AddWithValue("$hash", attempt.TransactionHash.Value);
        command.Parameters.AddWithValue("$block", $"0x{new string('b', 64)}");
        command.Parameters.AddWithValue("$time", OrchestratorTestData.Now.ToString("O"));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
