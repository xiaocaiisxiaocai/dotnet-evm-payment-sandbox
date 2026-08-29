using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Rpc;

namespace PaymentSandbox.Indexer.Tests.Infrastructure;

internal sealed class FakeChainObservationRpc : IChainObservationRpc
{
    internal BigInteger ChainId { get; set; } = IndexerTestData.ChainId.Value;

    internal Dictionary<long, RpcBlockHeader> Blocks { get; } = [];

    internal IReadOnlyList<RpcPaymentRecordedLog> Logs { get; set; } = [];

    internal Exception? ChainIdException { get; set; }

    internal int ChainIdCalls { get; private set; }

    internal int BlockCalls { get; private set; }

    internal int LogCalls { get; private set; }

    public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChainIdCalls++;
        if (ChainIdException is not null)
        {
            throw ChainIdException;
        }

        return Task.FromResult(ChainId);
    }

    public Task<RpcBlockHeader?> GetBlockAsync(
        long blockNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockCalls++;
        Blocks.TryGetValue(blockNumber, out RpcBlockHeader? block);
        return Task.FromResult(block);
    }

    public Task<IReadOnlyList<RpcPaymentRecordedLog>> GetPaymentRecordedLogsAsync(
        EvmAddress router,
        long fromBlockNumber,
        long toBlockNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogCalls++;
        return Task.FromResult(Logs);
    }
}
