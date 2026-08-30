using PaymentSandbox.Orchestrator.Infrastructure;

namespace PaymentSandbox.Orchestrator.Tests.Infrastructure;

public sealed class LocalAnvilRpcClientOptionsTests
{
    [Theory]
    [InlineData("https://mainnet.example/rpc")]
    [InlineData("http://192.0.2.10:8545")]
    [InlineData("http://user:secret@127.0.0.1:8545")]
    [InlineData("ws://127.0.0.1:8545")]
    public void Options_RejectNonLoopbackOrCredentialBearingEndpoints(string value)
    {
        Assert.Throws<ArgumentException>(() => new LocalAnvilRpcClientOptions(value));
    }

    [Fact]
    public void Options_AcceptBoundedLoopbackHttp()
    {
        var options = new LocalAnvilRpcClientOptions(
            "http://127.0.0.1:18546", TimeSpan.FromSeconds(5));

        Assert.True(options.RpcUri.IsLoopback);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RequestTimeout);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(31)]
    public void Options_RejectTimeoutsOutsideTheBoundedWindow(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalAnvilRpcClientOptions(
                "http://127.0.0.1:8545", TimeSpan.FromSeconds(seconds)));
    }
}
