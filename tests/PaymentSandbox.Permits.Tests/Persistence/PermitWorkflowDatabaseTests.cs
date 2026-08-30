using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Persistence;
using PaymentSandbox.Permits.Tests.Infrastructure;
using PaymentSandbox.Permits.Workflow;

namespace PaymentSandbox.Permits.Tests.Persistence;

public sealed class PermitWorkflowDatabaseTests
{
    [Fact]
    public async Task Migration_IsIdempotentStrictAndPinsCapacity()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var clock = new PermitWorkflowTestData.MutableTimeProvider(PermitWorkflowTestData.Now);
        var database = new PermitWorkflowDatabase(
            new PermitWorkflowDatabaseOptions(temporary.DatabasePath, capacity: 2), clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        var mismatch = new PermitWorkflowDatabase(
            new PermitWorkflowDatabaseOptions(temporary.DatabasePath, capacity: 3), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mismatch.InitializeAsync(TestContext.Current.CancellationToken));
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name IN (
                'permit_store_settings', 'permit_operations',
                'permit_preparations', 'permit_state_transitions')
              AND sql LIKE '%STRICT%';
            """;
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);

        await using SqliteCommand mutateCapacity = connection.CreateCommand();
        mutateCapacity.CommandText =
            "UPDATE permit_store_settings SET capacity = 3 WHERE singleton = 1;";
        await Assert.ThrowsAsync<SqliteException>(
            () => mutateCapacity.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SchemaRejectsMutationAndInvalidTransition()
    {
        await using var temporary = new TemporaryPermitDatabase();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowCommitResult reserved = await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(1), TestContext.Current.CancellationToken);
        await using SqliteConnection connection = await components.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);

        await using SqliteCommand update = connection.CreateCommand();
        update.CommandText =
            "UPDATE permit_operations SET value_raw = '2' WHERE operation_id = $operation;";
        update.Parameters.AddWithValue("$operation", reserved.Snapshot.OperationId.Value);
        await Assert.ThrowsAsync<SqliteException>(
            () => update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        await using SqliteCommand invalid = connection.CreateCommand();
        invalid.CommandText =
            """
            INSERT INTO permit_state_transitions (
                operation_id, kind, observed_block_number, observed_block_hash,
                observed_nonce, occurred_at_utc)
            VALUES ($operation, 'submission_accepted', NULL, NULL, NULL, $time);
            """;
        invalid.Parameters.AddWithValue("$operation", reserved.Snapshot.OperationId.Value);
        invalid.Parameters.AddWithValue("$time", PermitWorkflowTestData.Now.ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(
            () => invalid.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        await components.Workflow.VerifyAndPrepareAsync(
            reserved.Snapshot.OperationId,
            wallet.Sign(reserved.Snapshot.Draft),
            await PermitWorkflowTestData.RouterAsync(),
            PaymentId.New(),
            EvmAddress.Parse(PermitWorkflowTestData.MerchantAddress),
            TestContext.Current.CancellationToken);
        await using SqliteCommand missingObservation = connection.CreateCommand();
        missingObservation.CommandText =
            """
            INSERT INTO permit_state_transitions (
                operation_id, kind, observed_block_number, observed_block_hash,
                observed_nonce, occurred_at_utc)
            VALUES ($operation, 'submission_unknown', NULL, NULL, NULL, $time);
            """;
        missingObservation.Parameters.AddWithValue(
            "$operation", reserved.Snapshot.OperationId.Value);
        missingObservation.Parameters.AddWithValue(
            "$time", PermitWorkflowTestData.Now.ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(
            () => missingObservation.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CapacityFailurePreservesExistingOperation()
    {
        await using var temporary = new TemporaryPermitDatabase();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary, capacity: 1);
        var firstWallet = new PermitWorkflowTestData.TestWallet();
        var secondWallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowCommitResult first = await components.Workflow.ReserveAsync(
            firstWallet.Address, new RawTokenAmount(1), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PermitWorkflowException>(() =>
            components.Workflow.ReserveAsync(
                secondWallet.Address, new RawTokenAmount(1),
                TestContext.Current.CancellationToken));

        Assert.NotNull(await components.Store.GetAsync(
            first.Snapshot.OperationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRejectsInternallyInconsistentSignatureBearingCalldata()
    {
        await using var temporary = new TemporaryPermitDatabase();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowCommitResult reserved = await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(10), TestContext.Current.CancellationToken);
        await components.Workflow.VerifyAndPrepareAsync(
            reserved.Snapshot.OperationId,
            wallet.Sign(reserved.Snapshot.Draft),
            await PermitWorkflowTestData.RouterAsync(),
            PaymentId.New(),
            EvmAddress.Parse(PermitWorkflowTestData.MerchantAddress),
            TestContext.Current.CancellationToken);

        // Simulate corruption below SQLite's normal immutable-row guard. The
        // read path must still compare the bytes with their persisted hash.
        await using SqliteConnection connection = await components.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand corrupt = connection.CreateCommand();
        corrupt.CommandText =
            """
            DROP TRIGGER permit_preparations_immutable;
            UPDATE permit_preparations
            SET calldata_hash = '0x0000000000000000000000000000000000000000000000000000000000000000'
            WHERE operation_id = $operation;
            """;
        corrupt.Parameters.AddWithValue("$operation", reserved.Snapshot.OperationId.Value);
        await corrupt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PermitWorkflowException>(async () =>
            await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));
    }
}
