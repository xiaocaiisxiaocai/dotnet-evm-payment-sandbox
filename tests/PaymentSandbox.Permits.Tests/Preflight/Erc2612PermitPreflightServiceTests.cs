using Nethereum.Util;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Permits.Preflight;
using PaymentSandbox.Permits.Tests.Infrastructure;

namespace PaymentSandbox.Permits.Tests.Preflight;

public sealed class Erc2612PermitPreflightServiceTests
{
    [Fact]
    public async Task ExactSnapshot_MatchesChainCodeNameDomainAndOwnerNonce()
    {
        Erc2612TokenTrustPolicy policy = PermitWorkflowTestData.TrustPolicy();
        var rpc = new PermitWorkflowTestData.MutableTokenSnapshotRpc(policy, nonce: 42);
        var service = new Erc2612PermitPreflightService(policy, rpc);
        EvmAddress owner = EvmAddress.Parse("0x5555555555555555555555555555555555555555");

        VerifiedErc2612TokenSnapshot result = await service.ObserveAsync(
            owner, TestContext.Current.CancellationToken);

        Assert.Equal(owner, result.Owner);
        Assert.Equal(42, result.Nonce);
        Assert.Equal(101, result.BlockNumber);
        Assert.Equal(policy.ExpectedRuntimeCodeHash, result.RuntimeCodeHash);
        Assert.Equal(66, result.DomainSeparator.Length);
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("domain")]
    public async Task AnyIdentityMismatch_FailsClosed(string changedFact)
    {
        Erc2612TokenTrustPolicy policy = PermitWorkflowTestData.TrustPolicy();
        var rpc = new PermitWorkflowTestData.MutableTokenSnapshotRpc(policy, nonce: 1);
        Erc2612PreflightErrorCode expected = changedFact switch
        {
            "chain" => Change(() => rpc.ChainId = 11_155_111,
                Erc2612PreflightErrorCode.ChainMismatch),
            "code" => Change(() => rpc.RuntimeCode = "0x6001",
                Erc2612PreflightErrorCode.RuntimeCodeMismatch),
            "name" => Change(() => rpc.TokenName = "Other",
                Erc2612PreflightErrorCode.TokenNameMismatch),
            "domain" => Change(() => rpc.DomainSeparatorOverride = $"0x{new string('a', 64)}",
                Erc2612PreflightErrorCode.DomainSeparatorMismatch),
            _ => throw new InvalidOperationException(),
        };
        var service = new Erc2612PermitPreflightService(policy, rpc);

        Erc2612PreflightException exception = await Assert.ThrowsAsync<Erc2612PreflightException>(
            () => service.ObserveAsync(
                EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
                TestContext.Current.CancellationToken));

        Assert.Equal(expected, exception.Code);
    }

    [Fact]
    public async Task AdapterException_IsSanitized()
    {
        Erc2612TokenTrustPolicy policy = PermitWorkflowTestData.TrustPolicy();
        var service = new Erc2612PermitPreflightService(
            policy, new ThrowingRpc("https://user:secret@example.invalid"));

        Erc2612PreflightException exception = await Assert.ThrowsAsync<Erc2612PreflightException>(
            () => service.ObserveAsync(
                EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
                TestContext.Current.CancellationToken));

        Assert.Equal(Erc2612PreflightErrorCode.ObservationFailed, exception.Code);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static Erc2612PreflightErrorCode Change(
        Action action,
        Erc2612PreflightErrorCode expected)
    {
        action();
        return expected;
    }

    private sealed class ThrowingRpc(string sensitive) : IErc2612TokenSnapshotRpc
    {
        public Task<Erc2612TokenSnapshotObservation> ObserveAsync(
            EvmAddress token,
            EvmAddress owner,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(sensitive);
    }
}
