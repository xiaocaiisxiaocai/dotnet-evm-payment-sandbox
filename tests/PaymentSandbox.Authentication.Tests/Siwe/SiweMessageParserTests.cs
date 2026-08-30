using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;

namespace PaymentSandbox.Authentication.Tests.Siwe;

public sealed class SiweMessageParserTests
{
    [Fact]
    public async Task IssuedChallenge_RendersOneCanonicalRoundTrippableMessage()
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();

        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string raw = challenge.CreateMessage(wallet.Address);
        SiweMessage parsed = SiweMessageParser.Parse(raw);

        Assert.Equal("auth.example", parsed.Domain);
        Assert.Equal(wallet.Address, parsed.Address);
        Assert.Equal("https://auth.example/login", parsed.RequestUri.AbsoluteUri);
        Assert.Equal(32, parsed.Nonce.Length);
        Assert.Matches("^[0-9a-f]{32}$", parsed.Nonce);
        Assert.Equal(AuthenticationTestData.StartTime, parsed.IssuedAtUtc);
        Assert.Equal(AuthenticationTestData.StartTime.AddMinutes(5), parsed.ExpirationTimeUtc);
        Assert.Equal(raw, parsed.Render());
        Assert.DoesNotContain(parsed.Nonce, parsed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(parsed.Nonce, challenge.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedMessages))]
    public void Parser_RejectsNonCanonicalOrUnsupportedMessages(string value)
    {
        SiweAuthenticationException exception = Assert.Throws<SiweAuthenticationException>(
            () => SiweMessageParser.Parse(value));

        Assert.Equal(SiweAuthenticationErrorCode.MalformedMessage, exception.Code);
        Assert.DoesNotContain("32891756", exception.ToString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> MalformedMessages()
    {
        string canonical = CanonicalMessage();
        yield return [canonical.Replace("\n", "\r\n", StringComparison.Ordinal)];
        yield return [canonical.Replace(
            "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2",
            "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
            StringComparison.Ordinal)];
        yield return [canonical.Replace("Version: 1", "Version: 2", StringComparison.Ordinal)];
        yield return [canonical.Replace("Chain ID: 31337", "Chain ID: 031337", StringComparison.Ordinal)];
        yield return [canonical.Replace("Nonce: 32891756", "Nonce: short7", StringComparison.Ordinal)];
        yield return [canonical.Replace(
            "Issued At: 2026-08-30T06:00:00Z",
            "Issued At: 2026-08-30T06:00:00.000Z",
            StringComparison.Ordinal)];
        yield return [canonical.Replace(
            "URI: https://auth.example/login",
            "URI: https://auth.example/login?redirect=evil",
            StringComparison.Ordinal)];
        yield return [canonical.Replace("Sign in", "登录", StringComparison.Ordinal)];
        yield return [$"{canonical}\nResources:\n- https://auth.example/profile"];
        yield return [$"{canonical}\n"];
    }

    private static string CanonicalMessage() =>
        """
        auth.example wants you to sign in with your Ethereum account:
        0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2

        Sign in to the dotnet EVM payment sandbox.

        URI: https://auth.example/login
        Version: 1
        Chain ID: 31337
        Nonce: 32891756
        Issued At: 2026-08-30T06:00:00Z
        Expiration Time: 2026-08-30T06:05:00Z
        """;
}
