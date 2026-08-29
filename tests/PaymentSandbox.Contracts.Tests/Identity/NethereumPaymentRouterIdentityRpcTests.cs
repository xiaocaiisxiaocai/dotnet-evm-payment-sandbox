using PaymentSandbox.Contracts.Identity;

namespace PaymentSandbox.Contracts.Tests.Identity;

public sealed class NethereumPaymentRouterIdentityRpcTests
{
    [Theory]
    [InlineData("")]
    [InlineData("localhost:8545")]
    [InlineData("ws://localhost:8545")]
    [InlineData("file:///tmp/rpc")]
    public void Constructor_RejectsNonHttpEndpoint(string rpcUrl)
    {
        Assert.Throws<ArgumentException>(
            () => new NethereumPaymentRouterIdentityRpc(rpcUrl));
    }

    [Theory]
    [InlineData("http://localhost:8545")]
    [InlineData("https://rpc.example.test/v1/key")]
    public void Constructor_AcceptsAbsoluteHttpEndpointWithoutContactingIt(string rpcUrl)
    {
        var rpc = new NethereumPaymentRouterIdentityRpc(rpcUrl);

        Assert.NotNull(rpc);
    }
}
