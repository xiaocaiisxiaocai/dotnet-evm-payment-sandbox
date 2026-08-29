using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Processing;
using PaymentSandbox.Indexer.Rpc;

namespace PaymentSandbox.Indexer.Tests.Chain;

public sealed class ChainValueTests
{
    [Fact]
    public void EvmHash_NormalizesCasingAndPermitsZero()
    {
        EvmHash hash = EvmHash.Parse($"0X{new string('A', 64)}");
        EvmHash zero = EvmHash.Parse($"0x{new string('0', 64)}");

        Assert.Equal($"0x{new string('a', 64)}", hash.Value);
        Assert.Equal($"0x{new string('0', 64)}", zero.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x12")]
    [InlineData("0y1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("0xgggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void EvmHash_RejectsMalformedText(string value)
    {
        Assert.False(EvmHash.TryParse(value, out _));
        Assert.Throws<FormatException>(() => EvmHash.Parse(value));
    }

    [Fact]
    public void Policy_RejectsZeroRouterAndUnboundedRanges()
    {
        EvmAddress zero = EvmAddress.Parse("0x0000000000000000000000000000000000000000");
        EvmAddress router = EvmAddress.Parse("0x1111111111111111111111111111111111111111");

        Assert.Throws<ArgumentException>(() =>
            new ChainObservationPolicy(new EvmChainId(1), zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainObservationPolicy(new EvmChainId(1), router, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainObservationPolicy(new EvmChainId(1), router, 0, 10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainObservationPolicy(new EvmChainId(1), router, 0, maxLogsPerBatch: 100_001));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:8545")]
    [InlineData("ws://localhost:8545")]
    [InlineData("file:///tmp/rpc")]
    public void RpcConstructor_RejectsNonHttpEndpoint(string rpcUrl)
    {
        Assert.Throws<ArgumentException>(() => new NethereumChainObservationRpc(rpcUrl));
    }

    [Fact]
    public void RpcConstructor_DoesNotContactValidEndpoint()
    {
        var rpc = new NethereumChainObservationRpc("http://127.0.0.1:18545");

        Assert.NotNull(rpc);
    }
}
