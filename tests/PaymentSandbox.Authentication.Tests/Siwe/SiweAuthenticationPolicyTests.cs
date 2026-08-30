using System.Numerics;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Tests.Siwe;

public sealed class SiweAuthenticationPolicyTests
{
    [Theory]
    [InlineData("http://auth.example", "https://auth.example/login")]
    [InlineData("https://user:secret@auth.example", "https://auth.example/login")]
    [InlineData("https://auth.example/path", "https://auth.example/login")]
    [InlineData("https://auth.example", "https://other.example/login")]
    [InlineData("https://auth.example", "https://auth.example/login?next=/admin")]
    public void Policy_RejectsUnsafeOriginOrRequestUri(string origin, string requestUri)
    {
        Assert.Throws<ArgumentException>(() => AuthenticationTestData.Policy(origin, requestUri));
    }

    [Fact]
    public void Policy_RejectsMainnetRatherThanUsingANegativeNotMainnetCheck()
    {
        Assert.Throws<ArgumentException>(() => new SiweAuthenticationPolicy(
            new Uri("https://auth.example"),
            new Uri("https://auth.example/login"),
            new EvmChainId(BigInteger.One),
            "Sign in to the dotnet EVM payment sandbox."));
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("contains \\ a backslash")]
    [InlineData("包含非 ASCII")]
    public void Policy_RejectsStatementsOutsideTheBoundedAbnfSubset(string statement)
    {
        Assert.Throws<ArgumentException>(() => AuthenticationTestData.Policy(statement: statement));
    }

    [Fact]
    public void EquivalentPolicies_HaveOneStableMeaningFingerprint()
    {
        SiweAuthenticationPolicy first = AuthenticationTestData.Policy();
        SiweAuthenticationPolicy second = AuthenticationTestData.Policy();

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
    }
}
