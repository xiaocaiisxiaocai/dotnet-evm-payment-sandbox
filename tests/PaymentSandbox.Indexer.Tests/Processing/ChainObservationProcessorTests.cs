using Microsoft.Data.Sqlite;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Indexer.Processing;
using PaymentSandbox.Indexer.Tests.Infrastructure;

namespace PaymentSandbox.Indexer.Tests.Processing;

public sealed class ChainObservationProcessorTests
{
    [Fact]
    public async Task ScanThrough_ValidRangePersistsBlocksPaymentAndCheckpoint()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        ChainObservationProcessor processor = CreateProcessor(database, rpc);

        ChainObservationResult result = await processor.ScanThroughAsync(
            101,
            TestContext.Current.CancellationToken);

        Assert.Equal(ChainObservationDisposition.Applied, result.Disposition);
        Assert.Equal(2, result.ObservedBlockCount);
        Assert.Equal(1, result.ObservedPaymentCount);
        Assert.Equal(101, result.Checkpoint!.LastBlockNumber);
        Assert.Equal(IndexerTestData.Hash('2'), result.Checkpoint.LastBlockHash);
        Assert.Equal(1, result.Checkpoint.Revision);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountAsync(connection, "observed_blocks"));
        Assert.Equal(1, await CountAsync(connection, "payment_recorded_observations"));
    }

    [Fact]
    public async Task WrongChain_FailsBeforeReadingBlocksAndDoesNotCreateCheckpoint()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.ChainId = 1;
        ChainObservationProcessor processor = CreateProcessor(database, rpc);

        await Assert.ThrowsAsync<ChainObservationException>(() => processor.ScanThroughAsync(
            101,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, rpc.ChainIdCalls);
        Assert.Equal(0, rpc.BlockCalls);
        Assert.Null(await new SqliteChainObservationStore(database).GetCheckpointAsync(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ParentMismatch_FailsBeforeLogsAndDoesNotAdvanceCheckpoint()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.Blocks[101] = IndexerTestData.RpcBlock(101, '2', 'f');
        ChainObservationProcessor processor = CreateProcessor(database, rpc);

        ChainObservationException exception = await Assert.ThrowsAsync<ChainObservationException>(
            () => processor.ScanThroughAsync(101, TestContext.Current.CancellationToken));

        Assert.Contains("does not extend", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, rpc.LogCalls);
        Assert.Null(await new SqliteChainObservationStore(database).GetCheckpointAsync(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingBlock_FailsClosed()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.Blocks.Remove(101);

        ChainObservationException exception = await Assert.ThrowsAsync<ChainObservationException>(
            () => CreateProcessor(database, rpc).ScanThroughAsync(
                101,
                TestContext.Current.CancellationToken));

        Assert.Contains("no block 101", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedLog_FailsWithoutPersistingTheAlreadyReadBlocks()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.Logs = [IndexerTestData.RpcPayment(removed: true)];

        await Assert.ThrowsAsync<ChainObservationException>(() => CreateProcessor(database, rpc)
            .ScanThroughAsync(101, TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, await CountAsync(connection, "observed_blocks"));
        Assert.Equal(0, await CountAsync(connection, "payment_recorded_observations"));
    }

    [Fact]
    public async Task LogWithDifferentBlockHash_FailsClosed()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.Logs = [IndexerTestData.RpcPayment(blockHash: 'f')];

        ChainObservationException exception = await Assert.ThrowsAsync<ChainObservationException>(
            () => CreateProcessor(database, rpc).ScanThroughAsync(
                101,
                TestContext.Current.CancellationToken));

        Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RangeAboveConfiguredLimit_IsRejectedBeforeRpc()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        var policy = new ChainObservationPolicy(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            100,
            maxBatchSize: 2);
        var processor = new ChainObservationProcessor(
            policy,
            rpc,
            new SqliteChainObservationStore(database),
            new IndexerTestData.FixedTimeProvider(IndexerTestData.Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => processor.ScanThroughAsync(
            102,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, rpc.ChainIdCalls);
    }

    [Fact]
    public async Task TooManyLogs_FailsBeforePersistence()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc rpc = CreateReadyRpc();
        rpc.Logs = [IndexerTestData.RpcPayment(), IndexerTestData.RpcPayment(blockNumber: 100, blockHash: '1')];
        var processor = new ChainObservationProcessor(
            new ChainObservationPolicy(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                100,
                maxBatchSize: 10,
                maxLogsPerBatch: 1),
            rpc,
            new SqliteChainObservationStore(database),
            new IndexerTestData.FixedTimeProvider(IndexerTestData.Now));

        ChainObservationException exception = await Assert.ThrowsAsync<ChainObservationException>(
            () => processor.ScanThroughAsync(101, TestContext.Current.CancellationToken));

        Assert.Contains("configured maximum", exception.Message, StringComparison.Ordinal);
        Assert.Null(await new SqliteChainObservationStore(database).GetCheckpointAsync(
            IndexerTestData.ChainId,
            IndexerTestData.Router,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RpcFailure_IsWrappedWithoutLosingCause()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var cause = new HttpRequestException("endpoint unavailable");
        var rpc = new FakeChainObservationRpc { ChainIdException = cause };

        ChainObservationException exception = await Assert.ThrowsAsync<ChainObservationException>(
            () => CreateProcessor(database, rpc).ScanThroughAsync(
                100,
                TestContext.Current.CancellationToken));

        Assert.Same(cause, exception.InnerException);
        Assert.Contains("chain ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaximumLongBlockNumber_CompletesWithoutLoopOverflow()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var rpc = new FakeChainObservationRpc();
        rpc.Blocks[long.MaxValue] = new(
            long.MaxValue,
            IndexerTestData.Hash('e').Value,
            IndexerTestData.Hash('d').Value);
        var processor = new ChainObservationProcessor(
            new ChainObservationPolicy(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                long.MaxValue,
                maxBatchSize: 1),
            rpc,
            new SqliteChainObservationStore(database),
            new IndexerTestData.FixedTimeProvider(IndexerTestData.Now));

        ChainObservationResult result = await processor.ScanThroughAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(long.MaxValue, result.Checkpoint!.LastBlockNumber);
        Assert.Equal(1, result.ObservedBlockCount);
    }

    [Fact]
    public async Task AlreadyScannedTarget_ReturnsNoWorkWithoutRpc()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        FakeChainObservationRpc firstRpc = CreateReadyRpc();
        await CreateProcessor(database, firstRpc).ScanThroughAsync(
            101,
            TestContext.Current.CancellationToken);
        var secondRpc = new FakeChainObservationRpc();

        ChainObservationResult result = await CreateProcessor(database, secondRpc).ScanThroughAsync(
            101,
            TestContext.Current.CancellationToken);

        Assert.Equal(ChainObservationDisposition.NoWork, result.Disposition);
        Assert.Equal(101, result.Checkpoint!.LastBlockNumber);
        Assert.Equal(0, secondRpc.ChainIdCalls);
    }

    [Fact]
    public async Task NextBatchThatDoesNotExtendCheckpoint_FailsWithoutMovingCursor()
    {
        await using var temporary = new TemporaryIndexerDatabase();
        IndexerDatabase database = IndexerTestData.CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await CreateProcessor(database, CreateReadyRpc()).ScanThroughAsync(
            101,
            TestContext.Current.CancellationToken);
        var forkedRpc = new FakeChainObservationRpc();
        forkedRpc.Blocks[102] = IndexerTestData.RpcBlock(102, '3', 'f');

        await Assert.ThrowsAsync<ChainObservationException>(() =>
            CreateProcessor(database, forkedRpc).ScanThroughAsync(
                102,
                TestContext.Current.CancellationToken));

        ChainObservationCheckpoint? checkpoint = await new SqliteChainObservationStore(database)
            .GetCheckpointAsync(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                TestContext.Current.CancellationToken);
        Assert.Equal(101, checkpoint!.LastBlockNumber);
        Assert.Equal(1, checkpoint.Revision);
    }

    private static FakeChainObservationRpc CreateReadyRpc()
    {
        var rpc = new FakeChainObservationRpc();
        rpc.Blocks[100] = IndexerTestData.RpcBlock(100, '1', '0');
        rpc.Blocks[101] = IndexerTestData.RpcBlock(101, '2', '1');
        rpc.Logs = [IndexerTestData.RpcPayment()];
        return rpc;
    }

    private static ChainObservationProcessor CreateProcessor(
        IndexerDatabase database,
        FakeChainObservationRpc rpc) =>
        new(
            new ChainObservationPolicy(
                IndexerTestData.ChainId,
                IndexerTestData.Router,
                startBlockNumber: 100,
                maxBatchSize: 10),
            rpc,
            new SqliteChainObservationStore(database),
            new IndexerTestData.FixedTimeProvider(IndexerTestData.Now));

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
