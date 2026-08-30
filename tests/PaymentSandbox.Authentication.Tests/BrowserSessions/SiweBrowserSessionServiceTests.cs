using Microsoft.Data.Sqlite;
using PaymentSandbox.Authentication.BrowserSessions;
using PaymentSandbox.Authentication.Persistence;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;

namespace PaymentSandbox.Authentication.Tests.BrowserSessions;

public sealed class SiweBrowserSessionServiceTests
{
    [Fact]
    public async Task VerifyAsync_CreatesDurableSessionWithoutPersistingBearerSecrets()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, SiweChallengeDatabase database) =
            await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweLoginChallenge challenge = await service.IssueAsync(
            wallet.Address,
            TestContext.Current.CancellationToken);
        string signature = wallet.Sign(challenge.Message);

        SiweSessionCredentials credentials = await service.VerifyAsync(
            challenge.Message,
            signature,
            challenge.BrowserBindingToken,
            cancellationToken: TestContext.Current.CancellationToken);
        (SiweBrowserSessionService restarted, _) = await CreateServiceAsync(temporary, clock);
        SiweBrowserSession restored = await restarted.GetSessionAsync(
            credentials.SessionToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(wallet.Address, restored.Address);
        Assert.Equal(AuthenticationTestData.StartTime, restored.CreatedAtUtc);
        Assert.Equal(AuthenticationTestData.StartTime.AddMinutes(30), restored.ExpirationTimeUtc);
        Assert.True(SiweBrowserSessionService.IsCanonicalOpaqueToken(
            credentials.SessionToken));
        Assert.True(SiweBrowserSessionService.IsCanonicalOpaqueToken(
            credentials.CsrfToken));
        Assert.DoesNotContain(credentials.SessionToken, credentials.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.CsrfToken, credentials.ToString(),
            StringComparison.Ordinal);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_token_hash, csrf_token_hash FROM siwe_sessions;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(64, reader.GetString(0).Length);
        Assert.Equal(64, reader.GetString(1).Length);
        Assert.NotEqual(credentials.SessionToken, reader.GetString(0));
        Assert.NotEqual(credentials.CsrfToken, reader.GetString(1));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WrongBrowserBinding_DoesNotConsumeWalletProof()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, _) = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweLoginChallenge challenge = await service.IssueAsync(
            wallet.Address,
            TestContext.Current.CancellationToken);
        string signature = wallet.Sign(challenge.Message);
        string wrongBinding = new('a', 64);

        SiweBrowserSessionException rejected =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.VerifyAsync(
                challenge.Message,
                signature,
                wrongBinding,
                cancellationToken: TestContext.Current.CancellationToken));
        SiweSessionCredentials recovered = await service.VerifyAsync(
            challenge.Message,
            signature,
            challenge.BrowserBindingToken,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SiweBrowserSessionErrorCode.InvalidBrowserBinding, rejected.Code);
        Assert.Equal(wallet.Address, recovered.Session.Address);
    }

    [Fact]
    public async Task ConcurrentVerify_ConsumesOneFlowAndCreatesOneSession()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService issuer, _) = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweLoginChallenge challenge = await issuer.IssueAsync(
            wallet.Address,
            TestContext.Current.CancellationToken);
        string signature = wallet.Sign(challenge.Message);
        SiweBrowserSessionService[] services = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(async _ => (await CreateServiceAsync(temporary, clock)).Service));

        Task<object>[] attempts = services.Select(async service =>
        {
            try
            {
                return (object)await service.VerifyAsync(
                    challenge.Message,
                    signature,
                    challenge.BrowserBindingToken,
                    cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (Exception exception) when (
                exception is SiweAuthenticationException or SiweBrowserSessionException)
            {
                return exception;
            }
        }).ToArray();
        object[] outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes.OfType<SiweSessionCredentials>());
        Assert.Equal(11, outcomes.Count(value => value is Exception));
    }

    [Fact]
    public async Task Relogin_RotatesPreviousSessionInOneTransaction()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, _) = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweSessionCredentials first = await LoginAsync(service, wallet);
        SiweLoginChallenge secondChallenge = await service.IssueAsync(
            wallet.Address,
            TestContext.Current.CancellationToken);

        SiweSessionCredentials second = await service.VerifyAsync(
            secondChallenge.Message,
            wallet.Sign(secondChallenge.Message),
            secondChallenge.BrowserBindingToken,
            first.SessionToken,
            TestContext.Current.CancellationToken);
        SiweBrowserSessionException oldSession =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.GetSessionAsync(
                first.SessionToken,
                TestContext.Current.CancellationToken));
        SiweBrowserSession active = await service.GetSessionAsync(
            second.SessionToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(SiweBrowserSessionErrorCode.SessionRevoked, oldSession.Code);
        Assert.Equal(wallet.Address, active.Address);
        Assert.NotEqual(first.SessionToken, second.SessionToken);
    }

    [Fact]
    public async Task Logout_RequiresDoubleSubmitCsrfAndRevokesOneWay()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, _) = await CreateServiceAsync(temporary, clock);
        SiweSessionCredentials credentials = await LoginAsync(service, new TestEoa());

        SiweBrowserSessionException mismatch =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.LogoutAsync(
                credentials.SessionToken,
                credentials.CsrfToken,
                new string('b', 64),
                TestContext.Current.CancellationToken));
        await service.GetSessionAsync(
            credentials.SessionToken,
            TestContext.Current.CancellationToken);
        await service.LogoutAsync(
            credentials.SessionToken,
            credentials.CsrfToken,
            credentials.CsrfToken,
            TestContext.Current.CancellationToken);
        SiweBrowserSessionException revoked =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.GetSessionAsync(
                credentials.SessionToken,
                TestContext.Current.CancellationToken));

        Assert.Equal(SiweBrowserSessionErrorCode.CsrfMismatch, mismatch.Code);
        Assert.Equal(SiweBrowserSessionErrorCode.SessionRevoked, revoked.Code);
    }

    [Fact]
    public async Task ExactExpirationBoundary_IsNotActive()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, _) = await CreateServiceAsync(temporary, clock);
        SiweSessionCredentials credentials = await LoginAsync(service, new TestEoa());
        clock.Advance(TimeSpan.FromMinutes(30));

        SiweBrowserSessionException expired =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.GetSessionAsync(
                credentials.SessionToken,
                TestContext.Current.CancellationToken));

        Assert.Equal(SiweBrowserSessionErrorCode.SessionExpired, expired.Code);
    }

    [Fact]
    public async Task CapacityFailure_DoesNotRevokeUnrelatedActiveSession()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, _) = await CreateServiceAsync(
            temporary,
            clock,
            sessionCapacity: 1);
        SiweSessionCredentials first = await LoginAsync(service, new TestEoa());
        var secondWallet = new TestEoa();
        SiweLoginChallenge secondChallenge = await service.IssueAsync(
            secondWallet.Address,
            TestContext.Current.CancellationToken);

        SiweBrowserSessionException full =
            await Assert.ThrowsAsync<SiweBrowserSessionException>(() => service.VerifyAsync(
                secondChallenge.Message,
                secondWallet.Sign(secondChallenge.Message),
                secondChallenge.BrowserBindingToken,
                cancellationToken: TestContext.Current.CancellationToken));
        SiweBrowserSession stillActive = await service.GetSessionAsync(
            first.SessionToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(SiweBrowserSessionErrorCode.SessionCapacityExceeded, full.Code);
        Assert.Equal(first.Session.Address, stillActive.Address);
    }

    [Fact]
    public async Task Migration2_UsesStrictTablesAndOneWaySessionFacts()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        (SiweBrowserSessionService service, SiweChallengeDatabase database) =
            await CreateServiceAsync(temporary, clock);
        SiweSessionCredentials credentials = await LoginAsync(service, new TestEoa());
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);

        await using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText =
                """
                SELECT
                    (SELECT name FROM schema_migrations WHERE version = 2),
                    (SELECT sql FROM sqlite_schema WHERE name = 'siwe_login_flows'),
                    (SELECT sql FROM sqlite_schema WHERE name = 'siwe_sessions');
                """;
            await using SqliteDataReader reader = await schema.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("create_siwe_browser_sessions", reader.GetString(0));
            Assert.Contains("STRICT", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("binding_token_hash", reader.GetString(1), StringComparison.Ordinal);
            Assert.Contains("STRICT", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session_token_hash", reader.GetString(2), StringComparison.Ordinal);
            Assert.DoesNotContain("signature", reader.GetString(2),
                StringComparison.OrdinalIgnoreCase);
        }

        await using (SqliteCommand mutate = connection.CreateCommand())
        {
            mutate.CommandText =
                "UPDATE siwe_sessions SET address = $address WHERE revoked_at_unix_milliseconds IS NULL;";
            mutate.Parameters.AddWithValue(
                "$address",
                "0x1111111111111111111111111111111111111111");
            SqliteException immutable = await Assert.ThrowsAsync<SqliteException>(
                () => mutate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Contains("one-way", immutable.Message, StringComparison.Ordinal);
        }

        await service.LogoutAsync(
            credentials.SessionToken,
            credentials.CsrfToken,
            credentials.CsrfToken,
            TestContext.Current.CancellationToken);
        await using SqliteCommand unRevoke = connection.CreateCommand();
        unRevoke.CommandText =
            "UPDATE siwe_sessions SET revoked_at_unix_milliseconds = NULL;";
        SqliteException oneWay = await Assert.ThrowsAsync<SqliteException>(
            () => unRevoke.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Contains("one-way", oneWay.Message, StringComparison.Ordinal);
    }

    private static async Task<SiweSessionCredentials> LoginAsync(
        SiweBrowserSessionService service,
        TestEoa wallet)
    {
        SiweLoginChallenge challenge = await service.IssueAsync(
            wallet.Address,
            TestContext.Current.CancellationToken);
        return await service.VerifyAsync(
            challenge.Message,
            wallet.Sign(challenge.Message),
            challenge.BrowserBindingToken,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<(
        SiweBrowserSessionService Service,
        SiweChallengeDatabase Database)> CreateServiceAsync(
        TemporarySiweChallengeDatabase temporary,
        TimeProvider clock,
        int sessionCapacity = 1_024)
    {
        var options = new SiweChallengeDatabaseOptions(
            temporary.DatabasePath,
            capacity: 1_024,
            sessionCapacity);
        var database = new SiweChallengeDatabase(options, clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqliteSiweChallengeStore(database);
        var authentication = new SiweAuthenticationService(
            AuthenticationTestData.Policy(),
            store,
            clock);
        var sessions = new SiweBrowserSessionService(
            authentication,
            new SqliteSiweBrowserSessionStore(database),
            new SiweBrowserSessionPolicy(),
            clock);
        return (sessions, database);
    }
}
