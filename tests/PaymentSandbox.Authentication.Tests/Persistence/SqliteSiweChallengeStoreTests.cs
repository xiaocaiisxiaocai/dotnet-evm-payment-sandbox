using Microsoft.Data.Sqlite;
using PaymentSandbox.Authentication.Persistence;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;

namespace PaymentSandbox.Authentication.Tests.Persistence;

public sealed class SqliteSiweChallengeStoreTests
{
    [Fact]
    public async Task InitializeAsync_AppliesStrictOwnedSchemaIdempotently()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock);

        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.name, t.sql, trigger.sql
            FROM schema_migrations AS m
            JOIN sqlite_schema AS t
              ON t.type = 'table' AND t.name = 'siwe_challenges'
            JOIN sqlite_schema AS trigger
              ON trigger.type = 'trigger'
             AND trigger.name = 'siwe_challenge_consumption_is_one_way'
            WHERE m.version = 1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("create_siwe_challenge_store", reader.GetString(0));
        string tableSql = reader.GetString(1);
        Assert.Contains("STRICT", tableSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", tableSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", tableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-way", reader.GetString(2), StringComparison.Ordinal);
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentInitialize_AppliesMigrationExactlyOnce()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase first = CreateDatabase(temporary, clock);
        SiweChallengeDatabase second = CreateDatabase(temporary, clock);

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken),
            second.InitializeAsync(TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await first.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountAsync(connection, "schema_migrations"));
    }

    [Fact]
    public async Task RestartAfterIssue_AllowsOneConsumeAndRetainsReplayState()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweAuthenticationService issuer = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweChallenge challenge = await issuer.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        string signature = wallet.Sign(message);

        SiweAuthenticationService afterFirstRestart = await CreateServiceAsync(temporary, clock);
        SiweAuthenticationResult result = await afterFirstRestart.AuthenticateAsync(
            message, signature, TestContext.Current.CancellationToken);
        SiweAuthenticationService afterSecondRestart = await CreateServiceAsync(temporary, clock);
        SiweAuthenticationException replay = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => afterSecondRestart.AuthenticateAsync(
                message, signature, TestContext.Current.CancellationToken));

        Assert.Equal(wallet.Address, result.Address);
        Assert.Equal(SiweAuthenticationErrorCode.ChallengeAlreadyUsed, replay.Code);
    }

    [Fact]
    public async Task IndependentStoreInstances_ConsumeConcurrentProofExactlyOnce()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweAuthenticationService issuer = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweChallenge challenge = await issuer.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        string signature = wallet.Sign(message);
        SiweAuthenticationService[] verifiers = Enumerable.Range(0, 24)
            .Select(_ => CreateService(temporary, clock))
            .ToArray();

        Task<object>[] attempts = verifiers.Select(async verifier =>
        {
            try
            {
                return (object)await verifier.AuthenticateAsync(
                    message, signature, TestContext.Current.CancellationToken);
            }
            catch (SiweAuthenticationException exception)
            {
                return (object)exception.Code;
            }
        }).ToArray();
        object[] outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes.OfType<SiweAuthenticationResult>());
        Assert.Equal(23, outcomes.Count(value =>
            value is SiweAuthenticationErrorCode.ChallengeAlreadyUsed));
    }

    [Fact]
    public async Task IndependentIssuers_RespectOneSharedCapacityLimit()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock, capacity: 1);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        SiweAuthenticationService first = CreateService(temporary, clock, capacity: 1);
        SiweAuthenticationService second = CreateService(temporary, clock, capacity: 1);

        Task<object>[] attempts = [IssueAsync(first), IssueAsync(second)];
        object[] outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes.OfType<SiweChallenge>());
        Assert.Single(outcomes, value =>
            value is SiweAuthenticationErrorCode.ChallengeCapacityExceeded);

        static async Task<object> IssueAsync(SiweAuthenticationService service)
        {
            try
            {
                return await service.IssueChallengeAsync(TestContext.Current.CancellationToken);
            }
            catch (SiweAuthenticationException exception)
            {
                return exception.Code;
            }
        }
    }

    [Fact]
    public async Task ExactExpirationBoundary_RemainsExpiredAfterRestart()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweAuthenticationService issuer = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweChallenge challenge = await issuer.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string message = challenge.CreateMessage(wallet.Address);
        clock.Advance(TimeSpan.FromMinutes(5));
        SiweAuthenticationService restarted = await CreateServiceAsync(temporary, clock);

        SiweAuthenticationException exception = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => restarted.AuthenticateAsync(
                message, wallet.Sign(message), TestContext.Current.CancellationToken));

        Assert.Equal(SiweAuthenticationErrorCode.ChallengeExpired, exception.Code);
    }

    [Fact]
    public async Task CapacityCleanup_PrunesExpiredRowsBeforeIssuingAgain()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock, capacity: 1);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        SiweAuthenticationService service = CreateService(temporary, clock, capacity: 1);
        SiweChallenge expired = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        SiweChallenge replacement = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountAsync(connection, "siwe_challenges"));
        Assert.NotEqual(expired.Nonce, replacement.Nonce);
        Assert.False(await NonceExistsAsync(connection, expired.Nonce));
    }

    [Fact]
    public async Task ShiftedSignedTimes_DoNotReplaceDurableIssuedFacts()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweAuthenticationService issuer = await CreateServiceAsync(temporary, clock);
        var wallet = new TestEoa();
        SiweChallenge challenge = await issuer.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        string original = challenge.CreateMessage(wallet.Address);
        string shifted = original
            .Replace("2026-08-30T06:00:00Z", "2026-08-30T06:00:01Z", StringComparison.Ordinal)
            .Replace("2026-08-30T06:05:00Z", "2026-08-30T06:05:01Z", StringComparison.Ordinal);
        SiweAuthenticationService restarted = await CreateServiceAsync(temporary, clock);

        SiweAuthenticationException mismatch = await Assert.ThrowsAsync<SiweAuthenticationException>(
            () => restarted.AuthenticateAsync(
                shifted, wallet.Sign(shifted), TestContext.Current.CancellationToken));
        SiweAuthenticationResult exact = await restarted.AuthenticateAsync(
            original, wallet.Sign(original), TestContext.Current.CancellationToken);

        Assert.Equal(SiweAuthenticationErrorCode.PolicyMismatch, mismatch.Code);
        Assert.Equal(wallet.Address, exact.Address);
    }

    [Fact]
    public async Task Schema_RejectsInvalidNonceAndIssuedFactMutation()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        SiweAuthenticationService service = CreateService(temporary, clock);
        SiweChallenge challenge = await service.IssueChallengeAsync(
            TestContext.Current.CancellationToken);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand invalid = connection.CreateCommand();
        invalid.CommandText =
            """
            INSERT INTO siwe_challenges (
                nonce, domain, request_uri, chain_id, statement,
                issued_at_unix_seconds, expiration_at_unix_seconds,
                policy_fingerprint, consumed_at_unix_milliseconds)
            VALUES ('short', 'auth.example', 'https://auth.example/login', '31337',
                    'statement', 1, 2,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', NULL);
            """;
        SqliteException invalidNonce = await Assert.ThrowsAsync<SqliteException>(
            () => invalid.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        await using SqliteCommand mutate = connection.CreateCommand();
        mutate.CommandText =
            "UPDATE siwe_challenges SET statement = 'changed' WHERE nonce = $nonce;";
        mutate.Parameters.AddWithValue("$nonce", challenge.Nonce);
        SqliteException immutable = await Assert.ThrowsAsync<SqliteException>(
            () => mutate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(19, invalidNonce.SqliteErrorCode);
        Assert.Equal(19, immutable.SqliteErrorCode);
        Assert.Contains("one-way", immutable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RejectsUnknownFutureMigration()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES (3, 'future', '2026-08-30T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateDatabase(temporary, clock)
                .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RejectsCapacityMismatchAcrossInstances()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        await CreateDatabase(temporary, clock, capacity: 10)
            .InitializeAsync(TestContext.Current.CancellationToken);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateDatabase(temporary, clock, capacity: 11)
                .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("database-owned capacity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RejectsSessionCapacityMismatchAcrossInstances()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        await CreateDatabase(temporary, clock, sessionCapacity: 10)
            .InitializeAsync(TestContext.Current.CancellationToken);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateDatabase(temporary, clock, sessionCapacity: 11)
                .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("challenge or session capacity", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RejectsKnownVersionWithUnexpectedName()
    {
        await using var temporary = new TemporarySiweChallengeDatabase();
        var clock = new MutableTimeProvider(AuthenticationTestData.StartTime);
        SiweChallengeDatabase database = CreateDatabase(temporary, clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE schema_migrations SET name = 'unexpected' WHERE version = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateDatabase(temporary, clock)
                .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("expected 'create_siwe_challenge_store'", exception.Message,
            StringComparison.Ordinal);
    }

    private static SiweChallengeDatabase CreateDatabase(
        TemporarySiweChallengeDatabase temporary,
        TimeProvider clock,
        int capacity = 1_024,
        int sessionCapacity = 1_024) =>
        new(
            new SiweChallengeDatabaseOptions(
                temporary.DatabasePath,
                capacity,
                sessionCapacity),
            clock);

    private static SiweAuthenticationService CreateService(
        TemporarySiweChallengeDatabase temporary,
        TimeProvider clock,
        int capacity = 1_024)
    {
        SiweChallengeDatabase database = CreateDatabase(temporary, clock, capacity);
        return new SiweAuthenticationService(
            AuthenticationTestData.Policy(),
            new SqliteSiweChallengeStore(database),
            clock);
    }

    private static async Task<SiweAuthenticationService> CreateServiceAsync(
        TemporarySiweChallengeDatabase temporary,
        TimeProvider clock,
        int capacity = 1_024)
    {
        SiweChallengeDatabase database = CreateDatabase(temporary, clock, capacity);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        return new SiweAuthenticationService(
            AuthenticationTestData.Policy(),
            new SqliteSiweChallengeStore(database),
            clock);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<bool> NonceExistsAsync(
        SqliteConnection connection,
        string nonce)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM siwe_challenges WHERE nonce = $nonce);";
        command.Parameters.AddWithValue("$nonce", nonce);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! == 1;
    }
}
