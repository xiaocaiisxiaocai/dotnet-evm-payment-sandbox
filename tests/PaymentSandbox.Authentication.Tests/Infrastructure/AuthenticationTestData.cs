using Nethereum.Signer;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Tests.Infrastructure;

internal static class AuthenticationTestData
{
    internal static readonly DateTimeOffset StartTime =
        new(2026, 8, 30, 6, 0, 0, TimeSpan.Zero);

    internal static SiweAuthenticationPolicy Policy(
        string origin = "https://auth.example",
        string requestUri = "https://auth.example/login",
        string statement = "Sign in to the dotnet EVM payment sandbox.") =>
        new(
            new Uri(origin),
            new Uri(requestUri),
            new EvmChainId(SiweAuthenticationPolicy.LocalAnvilChainId),
            statement,
            challengeLifetime: TimeSpan.FromMinutes(5),
            allowedClockSkew: TimeSpan.FromSeconds(30));

    internal static (
        SiweAuthenticationService Service,
        InMemorySiweChallengeStore Store,
        MutableTimeProvider Clock) CreateService(
            SiweAuthenticationPolicy? policy = null,
            int capacity = 1_024)
    {
        var store = new InMemorySiweChallengeStore(capacity);
        var clock = new MutableTimeProvider(StartTime);
        return (new SiweAuthenticationService(
            policy ?? Policy(), store, clock), store, clock);
    }
}

internal sealed class TestEoa
{
    private readonly EthECKey _key = EthECKey.GenerateKey();

    internal EvmAddress Address => EvmAddress.Parse(_key.GetPublicAddress());

    internal string Sign(string message) =>
        new EthereumMessageSigner().EncodeUTF8AndSign(message, _key);
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan value) => _utcNow += value;
}
