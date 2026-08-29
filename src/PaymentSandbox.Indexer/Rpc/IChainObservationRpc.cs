using System.Numerics;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Rpc;

/// <summary>The read-only RPC surface needed by one explicit indexer batch.</summary>
/// <remarks>
/// The caller chooses exact block numbers. There is deliberately no account,
/// signing, broadcasting, receipt polling, or implicit "latest" scan operation.
/// </remarks>
public interface IChainObservationRpc
{
    Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default);

    Task<RpcBlockHeader?> GetBlockAsync(
        long blockNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RpcPaymentRecordedLog>> GetPaymentRecordedLogsAsync(
        EvmAddress router,
        long fromBlockNumber,
        long toBlockNumber,
        CancellationToken cancellationToken = default);
}
