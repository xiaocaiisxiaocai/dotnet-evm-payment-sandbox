using System.Net;
using System.Net.Http.Json;
using Nethereum.Signer;
using PaymentSandbox.Api.Authentication;
using PaymentSandbox.Api.Tests.Infrastructure;

namespace PaymentSandbox.Api.Tests.Authentication;

public sealed class SiweAuthenticationHttpTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoginThenSession_UsesHardenedOpaqueCookies()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(
            new MutableTimeProvider(Now));
        var wallet = new TestWallet();

        (SiweChallengeResponse challenge, string flowCookie, HttpResponseMessage issued) =
            await IssueAsync(host.Client, wallet.Address);
        using (issued)
        {
            string flowSetCookie = FindSetCookie(issued, SiweAuthenticationEndpoints.FlowCookieName);
            AssertHardenedCookie(flowSetCookie, httpOnly: true);
            Assert.DoesNotContain(flowCookie, challenge.Message, StringComparison.Ordinal);
        }

        using HttpResponseMessage verified = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            flowCookie);
        string sessionCookie = ExtractCookie(
            verified,
            SiweAuthenticationEndpoints.SessionCookieName);
        string csrfCookie = ExtractCookie(
            verified,
            SiweAuthenticationEndpoints.CsrfCookieName);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        AssertHardenedCookie(
            FindSetCookie(verified, SiweAuthenticationEndpoints.SessionCookieName),
            httpOnly: true);
        AssertHardenedCookie(
            FindSetCookie(verified, SiweAuthenticationEndpoints.CsrfCookieName),
            httpOnly: false);

        using HttpResponseMessage session = await GetSessionAsync(host.Client, sessionCookie);
        SiweSessionResponse body = await session.Content.ReadFromJsonAsync<SiweSessionResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Session response was empty.");

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(wallet.Address.ToLowerInvariant(), body.Address);
        Assert.Equal("31337", body.ChainId);
        Assert.Equal(Now, body.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(30), body.ExpirationTimeUtc);
        Assert.Equal("no-store", session.Headers.CacheControl?.ToString());
        Assert.Equal(64, sessionCookie.Length);
        Assert.Equal(64, csrfCookie.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    [InlineData("https://auth.example/")]
    public async Task Challenge_MissingOrNonExactOriginIsRejected(string? origin)
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/auth/siwe/challenge")
        {
            Content = JsonContent.Create(new IssueSiweChallengeRequest(
                "0x1111111111111111111111111111111111111111")),
        };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using HttpResponseMessage response = await host.Client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task WrongOrDuplicateFlowCookie_DoesNotBurnValidWalletProof()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(
            new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        (SiweChallengeResponse challenge, string flowCookie, HttpResponseMessage issued) =
            await IssueAsync(host.Client, wallet.Address);
        issued.Dispose();

        using HttpResponseMessage wrongOrigin = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            flowCookie,
            origin: "https://evil.example");
        using HttpResponseMessage wrong = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            new string('a', 64));
        using HttpResponseMessage duplicate = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            $"{flowCookie}; {SiweAuthenticationEndpoints.FlowCookieName}={flowCookie}");
        using HttpResponseMessage recovered = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            flowCookie);

        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Restart_PreservesUnverifiedFlowAndAuthenticatedSession()
    {
        await using var database = new TemporarySqliteDatabase();
        var clock = new MutableTimeProvider(Now);
        var wallet = new TestWallet();
        SiweChallengeResponse challenge;
        string flowCookie;

        await using (ApiTestHost issuer = await ApiTestHost.StartAsync(
            clock,
            database.DatabasePath,
            database.AuthenticationDatabasePath))
        {
            (challenge, flowCookie, HttpResponseMessage response) =
                await IssueAsync(issuer.Client, wallet.Address);
            response.Dispose();
        }

        string sessionCookie;
        await using (ApiTestHost verifier = await ApiTestHost.StartAsync(
            clock,
            database.DatabasePath,
            database.AuthenticationDatabasePath))
        {
            using HttpResponseMessage verified = await VerifyAsync(
                verifier.Client,
                challenge,
                wallet,
                flowCookie);
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            sessionCookie = ExtractCookie(
                verified,
                SiweAuthenticationEndpoints.SessionCookieName);
        }

        await using (ApiTestHost reader = await ApiTestHost.StartAsync(
            clock,
            database.DatabasePath,
            database.AuthenticationDatabasePath))
        {
            using HttpResponseMessage session = await GetSessionAsync(
                reader.Client,
                sessionCookie);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
    }

    [Fact]
    public async Task ReloginRotatesOldSessionAndLogoutRequiresCsrf()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(
            new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        (string firstSession, _) = await LoginAsync(host.Client, wallet);
        (SiweChallengeResponse challenge, string flow, HttpResponseMessage issued) =
            await IssueAsync(host.Client, wallet.Address);
        issued.Dispose();
        using HttpResponseMessage rotated = await VerifyAsync(
            host.Client,
            challenge,
            wallet,
            flow,
            firstSession);
        string secondSession = ExtractCookie(
            rotated,
            SiweAuthenticationEndpoints.SessionCookieName);
        string csrf = ExtractCookie(rotated, SiweAuthenticationEndpoints.CsrfCookieName);

        using HttpResponseMessage oldLookup = await GetSessionAsync(host.Client, firstSession);
        using HttpResponseMessage missingCsrf = await LogoutAsync(
            host.Client,
            secondSession,
            csrfCookie: null,
            csrfHeader: null);
        using HttpResponseMessage stillActive = await GetSessionAsync(
            host.Client,
            secondSession);
        using HttpResponseMessage loggedOut = await LogoutAsync(
            host.Client,
            secondSession,
            csrf,
            csrf);
        using HttpResponseMessage revoked = await GetSessionAsync(host.Client, secondSession);

        Assert.Equal(HttpStatusCode.Unauthorized, oldLookup.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task SessionExactExpirationBoundaryReturnsGeneric401()
    {
        var clock = new MutableTimeProvider(Now);
        await using ApiTestHost host = await ApiTestHost.StartAsync(clock);
        (string sessionCookie, _) = await LoginAsync(host.Client, new TestWallet());
        clock.Advance(TimeSpan.FromMinutes(30));

        using HttpResponseMessage expired = await GetSessionAsync(host.Client, sessionCookie);
        string body = await expired.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Contains("authentication_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullJsonBodiesReturnBoundedClientErrors()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using var challengeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/auth/siwe/challenge")
        {
            Content = new StringContent("null", null, "application/json"),
        };
        challengeRequest.Headers.TryAddWithoutValidation("Origin", "https://auth.example");
        using HttpResponseMessage challenge = await host.Client.SendAsync(
            challengeRequest,
            TestContext.Current.CancellationToken);

        using var verifyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/auth/siwe/verify")
        {
            Content = new StringContent("null", null, "application/json"),
        };
        verifyRequest.Headers.TryAddWithoutValidation("Origin", "https://auth.example");
        using HttpResponseMessage verify = await host.Client.SendAsync(
            verifyRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
    }

    private static async Task<(string Session, string Csrf)> LoginAsync(
        HttpClient client,
        TestWallet wallet)
    {
        (SiweChallengeResponse challenge, string flow, HttpResponseMessage issued) =
            await IssueAsync(client, wallet.Address);
        issued.Dispose();
        using HttpResponseMessage verified = await VerifyAsync(
            client,
            challenge,
            wallet,
            flow);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (
            ExtractCookie(verified, SiweAuthenticationEndpoints.SessionCookieName),
            ExtractCookie(verified, SiweAuthenticationEndpoints.CsrfCookieName));
    }

    private static async Task<(
        SiweChallengeResponse Challenge,
        string FlowCookie,
        HttpResponseMessage Response)> IssueAsync(
        HttpClient client,
        string address)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/auth/siwe/challenge")
        {
            Content = JsonContent.Create(new IssueSiweChallengeRequest(address)),
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://auth.example");
        HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SiweChallengeResponse challenge = await response.Content
            .ReadFromJsonAsync<SiweChallengeResponse>(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Challenge response was empty.");
        return (
            challenge,
            ExtractCookie(response, SiweAuthenticationEndpoints.FlowCookieName),
            response);
    }

    private static Task<HttpResponseMessage> VerifyAsync(
        HttpClient client,
        SiweChallengeResponse challenge,
        TestWallet wallet,
        string flowCookie,
        string? previousSession = null,
        string origin = "https://auth.example")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/siwe/verify")
        {
            Content = JsonContent.Create(new VerifySiweChallengeRequest(
                challenge.Message,
                wallet.Sign(challenge.Message))),
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        // Some negative tests deliberately pass an additional cookie segment
        // in flowCookie to prove duplicate-name ambiguity is rejected.
        string cookie = $"{SiweAuthenticationEndpoints.FlowCookieName}={flowCookie}";
        if (previousSession is not null)
        {
            cookie += $"; {SiweAuthenticationEndpoints.SessionCookieName}={previousSession}";
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> GetSessionAsync(
        HttpClient client,
        string sessionCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/session");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{SiweAuthenticationEndpoints.SessionCookieName}={sessionCookie}");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> LogoutAsync(
        HttpClient client,
        string sessionCookie,
        string? csrfCookie,
        string? csrfHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/logout");
        request.Headers.TryAddWithoutValidation("Origin", "https://auth.example");
        string cookie = $"{SiweAuthenticationEndpoints.SessionCookieName}={sessionCookie}";
        if (csrfCookie is not null)
        {
            cookie += $"; {SiweAuthenticationEndpoints.CsrfCookieName}={csrfCookie}";
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (csrfHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(
                SiweAuthenticationEndpoints.CsrfHeaderName,
                csrfHeader);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        string setCookie = FindSetCookie(response, name);
        string prefix = $"{name}=";
        int end = setCookie.IndexOf(';', prefix.Length);
        return setCookie[prefix.Length..end];
    }

    private static string FindSetCookie(HttpResponseMessage response, string name) =>
        Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{name}=", StringComparison.Ordinal));

    private static void AssertHardenedCookie(string value, bool httpOnly)
    {
        Assert.Contains("; secure", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", value, StringComparison.OrdinalIgnoreCase);
        if (httpOnly)
        {
            Assert.Contains("; httponly", value, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("; httponly", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TestWallet
    {
        private readonly EthECKey _key = EthECKey.GenerateKey();

        internal string Address => _key.GetPublicAddress();

        internal string Sign(string message) =>
            new EthereumMessageSigner().EncodeUTF8AndSign(message, _key);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan value) => _utcNow += value;
    }
}
