using System.Numerics;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using PaymentSandbox.Contracts.PaymentRouter.ContractDefinition;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Indexer.Rpc;

/// <summary>Nethereum adapter for exact block and PaymentRecorded observations.</summary>
public sealed class NethereumChainObservationRpc
    : IChainObservationRpc
{
    private readonly IWeb3 _web3;

    public NethereumChainObservationRpc(string rpcUrl)
        : this(CreateWeb3(rpcUrl))
    {
    }

    internal NethereumChainObservationRpc(IWeb3 web3)
    {
        _web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
    }

    public async Task<BigInteger> GetChainIdAsync(
        CancellationToken cancellationToken = default)
    {
        HexBigInteger chainId = await _web3.Eth.ChainId
            .SendRequestAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return chainId.Value;
    }

    public async Task<RpcBlockHeader?> GetBlockAsync(
        long blockNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        BlockWithTransactionHashes? block = await _web3.Eth.Blocks
            .GetBlockWithTransactionsHashesByNumber
            .SendRequestAsync(ToBlockParameter(blockNumber))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return block is null
            ? null
            : new RpcBlockHeader(
                block.Number?.Value ?? new BigInteger(-1),
                block.BlockHash,
                block.ParentHash);
    }

    public async Task<IReadOnlyList<RpcPaymentRecordedLog>> GetPaymentRecordedLogsAsync(
        EvmAddress router,
        long fromBlockNumber,
        long toBlockNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(fromBlockNumber);
        if (toBlockNumber < fromBlockNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toBlockNumber),
                "The final block cannot precede the first block.");
        }

        Event<PaymentRecordedEventDto> paymentEvent =
            _web3.Eth.GetEvent<PaymentRecordedEventDto>(router.Value);
        NewFilterInput filter = paymentEvent.CreateFilterInput(
            ToBlockParameter(fromBlockNumber),
            ToBlockParameter(toBlockNumber));
        List<EventLog<PaymentRecordedEventDto>> logs = await paymentEvent
            .GetAllChangesAsync(filter)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return logs.Select(log => new RpcPaymentRecordedLog(
                log.Log.Address,
                log.Log.BlockNumber?.Value ?? new BigInteger(-1),
                log.Log.BlockHash,
                log.Log.TransactionHash,
                log.Log.LogIndex?.Value ?? new BigInteger(-1),
                log.Log.Removed,
                log.Event.PaymentId,
                log.Event.Payer,
                log.Event.Token,
                log.Event.Merchant,
                log.Event.Amount))
            .ToArray();
    }

    private static BlockParameter ToBlockParameter(long blockNumber) =>
        new(new HexBigInteger(blockNumber));

    private static IWeb3 CreateWeb3(string rpcUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcUrl);
        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The RPC URL must be an absolute HTTP or HTTPS URL.",
                nameof(rpcUrl));
        }

        return new Web3(uri.AbsoluteUri);
    }
}
