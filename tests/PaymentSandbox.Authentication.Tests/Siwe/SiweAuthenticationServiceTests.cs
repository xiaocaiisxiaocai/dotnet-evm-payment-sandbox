using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;

namespace PaymentSandbox.Authentication.Tests.Siwe;

public sealed class SiweAuthenticationServiceTests
{
    [Fact]
    public async Task ValidErc191Signature_AuthenticatesOnceAndDoesNotCreateASession()
    {
        var (service, _, clock) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        string signature = wallet.Sign(message);

        SiweAuthenticationResult result = await service.AuthenticateAsync(
            message, signature, TestContext.Current.CancellationToken);
        SiweAuthenticationException replay = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                message, signature, TestContext.Current.CancellationToken));

        Assert.Equal(wallet.Address, result.Address);
        Assert.Equal(clock.GetUtcNow(), result.AuthenticatedAtUtc);
        Assert.Equal(SiweAuthenticationErrorCode.ChallengeAlreadyUsed, replay.Code);
        Assert.DoesNotContain(message, replay.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(signature, replay.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentReplay_ConsumesExactlyOnce()
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        string signature = wallet.Sign(message);

        Task<object>[] attempts = Enumerable.Range(0, 24)
            .Select(async _ =>
            {
                try
                {
                    return (object)await service.AuthenticateAsync(
                        message, signature, TestContext.Current.CancellationToken);
                }
                catch (SiweAuthenticationException exception)
                {
                    return (object)exception.Code;
                }
            })
            .ToArray();
        object[] outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes.OfType<SiweAuthenticationResult>());
        Assert.Equal(23, outcomes.Count(value =>
            value is SiweAuthenticationErrorCode.ChallengeAlreadyUsed));
    }

    [Fact]
    public async Task CrossDomainMessageWithValidSignature_FailsWithoutBurningOriginalChallenge()
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string original = challenge.CreateMessage(wallet.Address);
        string changed = original.Replace(
            "auth.example wants", "evil.example wants", StringComparison.Ordinal);

        SiweAuthenticationException mismatch = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                changed, wallet.Sign(changed), TestContext.Current.CancellationToken));
        SiweAuthenticationResult recovered = await service.AuthenticateAsync(
            original, wallet.Sign(original), TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.PolicyMismatch, mismatch.Code);
        Assert.Equal(wallet.Address, recovered.Address);
    }

    [Theory]
    [InlineData("URI: https://auth.example/login", "URI: https://auth.example/admin")]
    [InlineData("Chain ID: 31337", "Chain ID: 11155111")]
    [InlineData(
        "Sign in to the dotnet EVM payment sandbox.",
        "Sign in to a different relying party.")]
    public async Task ChangedPolicyFactWithValidSignature_FailsWithoutBurningChallenge(
        string expected,
        string replacement)
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string original = challenge.CreateMessage(wallet.Address);
        string changed = original.Replace(expected, replacement, StringComparison.Ordinal);

        SiweAuthenticationException mismatch = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                changed, wallet.Sign(changed), TestContext.Current.CancellationToken));
        SiweAuthenticationResult recovered = await service.AuthenticateAsync(
            original, wallet.Sign(original), TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.PolicyMismatch, mismatch.Code);
        Assert.Equal(wallet.Address, recovered.Address);
    }

    [Fact]
    public async Task ShiftedTimesWithSameValidLifetime_DoNotReplaceIssuedChallengeFacts()
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string original = challenge.CreateMessage(wallet.Address);
        string changed = original
            .Replace("2026-08-30T06:00:00Z", "2026-08-30T06:00:01Z", StringComparison.Ordinal)
            .Replace("2026-08-30T06:05:00Z", "2026-08-30T06:05:01Z", StringComparison.Ordinal);

        SiweAuthenticationException mismatch = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                changed, wallet.Sign(changed), TestContext.Current.CancellationToken));
        SiweAuthenticationResult recovered = await service.AuthenticateAsync(
            original, wallet.Sign(original), TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.PolicyMismatch, mismatch.Code);
        Assert.Equal(wallet.Address, recovered.Address);
    }

    [Fact]
    public async Task WrongSigner_FailsWithoutBurningOriginalChallenge()
    {
        var (service, _, _) = AuthenticationTestData.CreateService();
        var expectedWallet = new TestEoa();
        var wrongWallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(expectedWallet.Address);

        SiweAuthenticationException mismatch = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                message, wrongWallet.Sign(message), TestContext.Current.CancellationToken));
        SiweAuthenticationResult recovered = await service.AuthenticateAsync(
            message, expectedWallet.Sign(message), TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.InvalidSignature, mismatch.Code);
        Assert.Equal(expectedWallet.Address, recovered.Address);
    }

    [Fact]
    public async Task ExpiredChallenge_RejectsAnOtherwiseValidSignature()
    {
        var (service, _, clock) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        // Exercise the exact exclusive boundary, not merely one instant after it.
        clock.Advance(TimeSpan.FromMinutes(5));

        SiweAuthenticationException exception = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.AuthenticateAsync(
                message, wallet.Sign(message), TestContext.Current.CancellationToken));

        Assert.Equal(SiweAuthenticationErrorCode.ChallengeExpired, exception.Code);
    }

    [Fact]
    public async Task ChallengeFromAnotherStore_IsNotAccepted()
    {
        var (issuer, _, _) = AuthenticationTestData.CreateService();
        var (verifier, _, _) = AuthenticationTestData.CreateService();
        var wallet = new TestEoa();
        SiweChallenge challenge = await issuer.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);

        SiweAuthenticationException exception = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => verifier.AuthenticateAsync(
                message, wallet.Sign(message), TestContext.Current.CancellationToken));

        Assert.Equal(SiweAuthenticationErrorCode.ChallengeNotFound, exception.Code);
    }

    [Fact]
    public async Task CapacityLimit_FailsClosedUntilAnOldEntryCanBePruned()
    {
        var (service, _, clock) = AuthenticationTestData.CreateService(capacity: 1);
        await service.IssueChallengeAsync(TestContext.Current.CancellationToken);

        SiweAuthenticationException full = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => service.IssueChallengeAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(6));
        SiweChallenge afterExpiry = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.ChallengeCapacityExceeded, full.Code);
        Assert.Equal(clock.GetUtcNow(), afterExpiry.IssuedAtUtc);
    }
}
