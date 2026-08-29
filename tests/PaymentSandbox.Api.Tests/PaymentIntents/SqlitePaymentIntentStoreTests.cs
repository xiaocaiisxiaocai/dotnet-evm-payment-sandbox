using Microsoft.Data.Sqlite;
using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Api.Persistence;
using PaymentSandbox.Api.Tests.Infrastructure;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.Tests.PaymentIntents;

public sealed class SqlitePaymentIntentStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task InitializeAsync_AppliesVersionedStrictSchemaIdempotently()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);

        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.version, m.name, s.sql
            FROM schema_migrations AS m
            JOIN sqlite_schema AS s ON s.name = 'payment_intents';
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("create_payment_intents", reader.GetString(1));
        Assert.Contains("STRICT", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentInitialize_AppliesMigrationExactlyOnce()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase first = CreateDatabase(temporary.DatabasePath);
        PaymentIntentDatabase second = CreateDatabase(temporary.DatabasePath);

        await Task.WhenAll(
            first.InitializeAsync(TestContext.Current.CancellationToken),
            second.InitializeAsync(TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await first.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;";
        long count = (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Schema_RejectsNonCanonicalValuesWrittenOutsideApplication()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO payment_intents (
                payment_id, idempotency_key, chain_id, token_address,
                merchant_address, amount_raw, status, created_at_utc)
            VALUES (
                $paymentId, 'direct-write', '031337',
                '0x2222222222222222222222222222222222222222',
                '0x3333333333333333333333333333333333333333',
                '1', 'created', '2026-08-29T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$paymentId", PaymentId.New().Value);

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task NewStoreInstance_ReadsAndReplaysDurableIntent()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase firstDatabase = CreateDatabase(temporary.DatabasePath);
        await firstDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        var firstStore = new SqlitePaymentIntentStore(firstDatabase);
        IdempotencyKey key = ParseKey("durable-replay");
        PaymentIntent candidate = CreateIntent();

        PaymentIntentCreateResult created = await firstStore.CreateOrGetAsync(
            key,
            candidate,
            TestContext.Current.CancellationToken);

        PaymentIntentDatabase restartedDatabase = CreateDatabase(temporary.DatabasePath);
        await restartedDatabase.InitializeAsync(TestContext.Current.CancellationToken);
        var restartedStore = new SqlitePaymentIntentStore(restartedDatabase);
        PaymentIntentCreateResult replay = await restartedStore.CreateOrGetAsync(
            key,
            CreateIntent(token: EvmAddress.Parse(candidate.Terms.Token.Value.ToUpperInvariant())),
            TestContext.Current.CancellationToken);
        PaymentIntent? found = await restartedStore.FindByIdAsync(
            candidate.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Created, created.Disposition);
        Assert.Equal(PaymentIntentCreateDisposition.Replayed, replay.Disposition);
        Assert.Equal(candidate, replay.Intent);
        Assert.Equal(candidate, found);
    }

    [Fact]
    public async Task SameKeyWithDifferentTerms_ReturnsNonLeakingConflict()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqlitePaymentIntentStore(database);
        IdempotencyKey key = ParseKey("durable-conflict");
        await store.CreateOrGetAsync(
            key,
            CreateIntent(),
            TestContext.Current.CancellationToken);

        PaymentIntentCreateResult conflict = await store.CreateOrGetAsync(
            key,
            CreateIntent(amount: new RawTokenAmount(2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Conflict, conflict.Disposition);
        Assert.Null(conflict.Intent);
    }

    [Fact]
    public async Task ConcurrentIndependentConnections_PublishExactlyOneIntent()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        IdempotencyKey key = ParseKey("sqlite-race");

        Task<PaymentIntentCreateResult>[] attempts = Enumerable.Range(0, 20)
            .Select(_ => new SqlitePaymentIntentStore(database)
                .CreateOrGetAsync(
                    key,
                    CreateIntent(),
                    TestContext.Current.CancellationToken)
                .AsTask())
            .ToArray();
        PaymentIntentCreateResult[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result =>
            result.Disposition == PaymentIntentCreateDisposition.Created));
        Assert.Equal(19, results.Count(result =>
            result.Disposition == PaymentIntentCreateDisposition.Replayed));
        Assert.Single(results.Select(result => result.Intent!.Id).Distinct());
    }

    [Fact]
    public async Task CaseSensitiveKeys_CreateDifferentResources()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqlitePaymentIntentStore(database);

        PaymentIntentCreateResult lower = await store.CreateOrGetAsync(
            ParseKey("order-a"),
            CreateIntent(),
            TestContext.Current.CancellationToken);
        PaymentIntentCreateResult upper = await store.CreateOrGetAsync(
            ParseKey("ORDER-A"),
            CreateIntent(),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(lower.Intent!.Id, upper.Intent!.Id);
    }

    [Fact]
    public async Task CancellationBeforeCreate_DoesNotConsumeKey()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqlitePaymentIntentStore(database);
        IdempotencyKey key = ParseKey("cancelled-sqlite");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateOrGetAsync(
            key,
            CreateIntent(),
            cancellation.Token).AsTask());
        PaymentIntentCreateResult retry = await store.CreateOrGetAsync(
            key,
            CreateIntent(),
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Created, retry.Disposition);
    }

    [Fact]
    public async Task InitializeAsync_RejectsNewerUnknownSchemaVersion()
    {
        await using var temporary = new TemporarySqliteDatabase();
        PaymentIntentDatabase database = CreateDatabase(temporary.DatabasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO schema_migrations (version, name, applied_at_utc)
                VALUES (999, 'future_schema', '2026-08-29T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    private static PaymentIntentDatabase CreateDatabase(string path) =>
        new(new PaymentIntentDatabaseOptions(path), new FixedTimeProvider(Now));

    private static IdempotencyKey ParseKey(string value)
    {
        Assert.True(IdempotencyKey.TryParse(value, out IdempotencyKey? key));
        return key;
    }

    private static PaymentIntent CreateIntent(
        EvmAddress? token = null,
        RawTokenAmount? amount = null) =>
        PaymentIntent.Create(
            PaymentId.New(),
            new PaymentIntentTerms(
                new EvmChainId(31_337),
                token ?? EvmAddress.Parse("0x2222222222222222222222222222222222222222"),
                EvmAddress.Parse("0x3333333333333333333333333333333333333333"),
                amount ?? new RawTokenAmount(1)),
            Now);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
