using System.Numerics;
using PaymentSandbox.Contracts.Identity;

namespace PaymentSandbox.Contracts.Tests.Identity;

public sealed class PaymentRouterIdentityVerifierTests
{
    private const string RouterAddress = "0x1111111111111111111111111111111111111111";

    // Known Keccak-256 test vector for the single byte 0x00. Keeping the
    // expected digest outside the production hashing code avoids a tautology.
    private const string ZeroByteCodeHash =
        "0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a";

    [Fact]
    public async Task VerifyAsync_AcceptsMatchingChainAddressAndRuntimeCode()
    {
        var rpc = new FakeIdentityRpc(31_337, "0x00");
        var verifier = new PaymentRouterIdentityVerifier(rpc);
        var policy = new PaymentRouterTrustPolicy(31_337, RouterAddress, ZeroByteCodeHash);

        VerifiedPaymentRouterIdentity identity = await verifier.VerifyAsync(
            policy,
            TestContext.Current.CancellationToken);

        Assert.Equal(new BigInteger(31_337), identity.ChainId);
        Assert.Equal(RouterAddress, identity.ContractAddress);
        Assert.Equal(ZeroByteCodeHash, identity.RuntimeCodeKeccak256);
        Assert.Equal(["chainId", $"code:{RouterAddress}"], rpc.Calls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongChainBeforeRequestingCode()
    {
        var rpc = new FakeIdentityRpc(1, "0x00");
        var verifier = new PaymentRouterIdentityVerifier(rpc);

        PaymentRouterIdentityException exception = await Assert.ThrowsAsync<PaymentRouterIdentityException>(
            () => verifier.VerifyAsync(CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.Equal(PaymentRouterIdentityFailure.UnexpectedChainId, exception.Failure);
        Assert.Equal(["chainId"], rpc.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData(null)]
    public async Task VerifyAsync_RejectsMissingRuntimeCode(string? code)
    {
        var verifier = new PaymentRouterIdentityVerifier(new FakeIdentityRpc(31_337, code));

        PaymentRouterIdentityException exception = await Assert.ThrowsAsync<PaymentRouterIdentityException>(
            () => verifier.VerifyAsync(CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.Equal(PaymentRouterIdentityFailure.CodeMissing, exception.Failure);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("0x0")]
    [InlineData("0xzz")]
    [InlineData(" 0x00")]
    public async Task VerifyAsync_RejectsMalformedRuntimeCode(string code)
    {
        var verifier = new PaymentRouterIdentityVerifier(new FakeIdentityRpc(31_337, code));

        PaymentRouterIdentityException exception = await Assert.ThrowsAsync<PaymentRouterIdentityException>(
            () => verifier.VerifyAsync(CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.Equal(PaymentRouterIdentityFailure.CodeMalformed, exception.Failure);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnexpectedRuntimeCodeHash()
    {
        var verifier = new PaymentRouterIdentityVerifier(
            new FakeIdentityRpc(31_337, "0x01"));

        PaymentRouterIdentityException exception = await Assert.ThrowsAsync<PaymentRouterIdentityException>(
            () => verifier.VerifyAsync(CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.Equal(PaymentRouterIdentityFailure.RuntimeCodeHashMismatch, exception.Failure);
        Assert.Contains("expected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_WrapsRpcFailureWithIdentityContext()
    {
        var rpcFailure = new InvalidOperationException("synthetic endpoint failure");
        var rpc = new FakeIdentityRpc(31_337, "0x00")
        {
            ChainIdException = rpcFailure,
        };
        var verifier = new PaymentRouterIdentityVerifier(rpc);

        PaymentRouterIdentityException exception = await Assert.ThrowsAsync<PaymentRouterIdentityException>(
            () => verifier.VerifyAsync(CreatePolicy(), TestContext.Current.CancellationToken));

        Assert.Equal(PaymentRouterIdentityFailure.RpcRequestFailed, exception.Failure);
        Assert.Same(rpcFailure, exception.InnerException);
    }

    [Fact]
    public async Task VerifyAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var rpc = new FakeIdentityRpc(31_337, "0x00");
        var verifier = new PaymentRouterIdentityVerifier(rpc);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.VerifyAsync(CreatePolicy(), cancellation.Token));

        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public void TrustPolicy_NormalizesReviewedValues()
    {
        var policy = new PaymentRouterTrustPolicy(
            31_337,
            RouterAddress.ToUpperInvariant(),
            ZeroByteCodeHash.ToUpperInvariant());

        Assert.Equal(RouterAddress, policy.ContractAddress);
        Assert.Equal(ZeroByteCodeHash, policy.ExpectedRuntimeCodeKeccak256);
    }

    [Fact]
    public void TrustPolicy_RejectsInvalidLocalConfigurationBeforeRpcExists()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PaymentRouterTrustPolicy(0, RouterAddress, ZeroByteCodeHash));
        Assert.Throws<ArgumentException>(
            () => new PaymentRouterTrustPolicy(1, "0x0", ZeroByteCodeHash));
        Assert.Throws<ArgumentException>(
            () => new PaymentRouterTrustPolicy(1, $"0x{new string('0', 40)}", ZeroByteCodeHash));
        Assert.Throws<ArgumentException>(
            () => new PaymentRouterTrustPolicy(1, RouterAddress, "0x00"));
        Assert.Throws<ArgumentException>(
            () => new PaymentRouterTrustPolicy(1, RouterAddress, $"0x{new string('0', 64)}"));
    }

    private static PaymentRouterTrustPolicy CreatePolicy() =>
        new(31_337, RouterAddress, ZeroByteCodeHash);

    private sealed class FakeIdentityRpc : IPaymentRouterIdentityRpc
    {
        private readonly BigInteger _chainId;
        private readonly string? _code;

        public FakeIdentityRpc(BigInteger chainId, string? code)
        {
            _chainId = chainId;
            _code = code;
        }

        public List<string> Calls { get; } = [];

        public Exception? ChainIdException { get; init; }

        public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("chainId");
            if (ChainIdException is not null)
            {
                return Task.FromException<BigInteger>(ChainIdException);
            }

            return Task.FromResult(_chainId);
        }

        public Task<string> GetCodeAsync(
            string contractAddress,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"code:{contractAddress}");
            return Task.FromResult(_code!);
        }
    }
}
