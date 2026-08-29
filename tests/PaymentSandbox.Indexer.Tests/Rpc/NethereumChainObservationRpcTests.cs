using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Rpc;
using PaymentSandbox.Indexer.Tests.Infrastructure;

namespace PaymentSandbox.Indexer.Tests.Rpc;

public sealed class NethereumChainObservationRpcTests
{
    [Fact]
    public async Task Adapter_RequestsExactBlocksAndDecodesReviewedEventShape()
    {
        await using JsonRpcTestHost host = await JsonRpcTestHost.StartAsync();
        var rpc = new NethereumChainObservationRpc(host.Endpoint.AbsoluteUri);

        System.Numerics.BigInteger chainId = await rpc.GetChainIdAsync(
            TestContext.Current.CancellationToken);
        RpcBlockHeader? block = await rpc.GetBlockAsync(
            100,
            TestContext.Current.CancellationToken);
        IReadOnlyList<RpcPaymentRecordedLog> logs = await rpc.GetPaymentRecordedLogsAsync(
            IndexerTestData.Router,
            100,
            100,
            TestContext.Current.CancellationToken);

        Assert.Equal(31_337, chainId);
        Assert.NotNull(block);
        Assert.Equal(100, block.Number);
        Assert.Equal(IndexerTestData.Hash('1').Value, block.Hash);
        RpcPaymentRecordedLog payment = Assert.Single(logs);
        Assert.Equal(IndexerTestData.Router.Value, payment.ContractAddress);
        Assert.Equal(100, payment.BlockNumber);
        Assert.Equal(IndexerTestData.Hash('1').Value, payment.BlockHash);
        Assert.Equal(IndexerTestData.Hash('c').Value, payment.TransactionHash);
        Assert.Equal(3, payment.LogIndex);
        Assert.False(payment.Removed);
        Assert.Equal($"0x{new string('a', 64)}", ConvertToHex(payment.PaymentId!));
        Assert.Equal("0x4444444444444444444444444444444444444444", payment.Payer);
        Assert.Equal("0x2222222222222222222222222222222222222222", payment.Token);
        Assert.Equal("0x3333333333333333333333333333333333333333", payment.Merchant);
        Assert.Equal(1_250_000, payment.Amount);

        JsonRpcRequest[] requests = host.Requests.ToArray();
        Assert.Equal(["eth_chainId", "eth_getBlockByNumber", "eth_getLogs"],
            requests.Select(request => request.Method));
        Assert.Contains("\"0x64\"", requests[1].Parameters, StringComparison.Ordinal);
        Assert.Contains(IndexerTestData.Router.Value, requests[2].Parameters, StringComparison.Ordinal);
        Assert.Contains("\"fromBlock\":\"0x64\"", requests[2].Parameters, StringComparison.Ordinal);
        Assert.Contains("\"toBlock\":\"0x64\"", requests[2].Parameters, StringComparison.Ordinal);
    }

    private static string ConvertToHex(byte[] bytes) =>
        $"0x{Convert.ToHexString(bytes).ToLowerInvariant()}";
}
