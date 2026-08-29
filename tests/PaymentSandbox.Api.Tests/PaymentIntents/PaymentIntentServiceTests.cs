using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.Tests.PaymentIntents;

public sealed class PaymentIntentServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_SafelyReplaysSameNormalizedTerms()
    {
        var service = CreateService();
        var key = ParseKey("checkout-123");
        PaymentIntentTerms firstTerms = CreateTerms();
        PaymentIntentTerms equivalentTerms = CreateTerms(
            token: EvmAddress.Parse(firstTerms.Token.Value.ToUpperInvariant()));

        PaymentIntentCreateResult first = await service.CreateAsync(
            key,
            firstTerms,
            TestContext.Current.CancellationToken);
        PaymentIntentCreateResult replay = await service.CreateAsync(
            key,
            equivalentTerms,
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Created, first.Disposition);
        Assert.Equal(PaymentIntentCreateDisposition.Replayed, replay.Disposition);
        Assert.Same(first.Intent, replay.Intent);
        Assert.Equal(Now, replay.Intent!.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateAsync_RejectsSameKeyWithDifferentTermsWithoutLeakingIntent()
    {
        var service = CreateService();
        var key = ParseKey("checkout-conflict");
        await service.CreateAsync(
            key,
            CreateTerms(),
            TestContext.Current.CancellationToken);

        PaymentIntentCreateResult conflict = await service.CreateAsync(
            key,
            CreateTerms(amount: new RawTokenAmount(2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Conflict, conflict.Disposition);
        Assert.Null(conflict.Intent);
    }

    [Fact]
    public async Task CreateAsync_DifferentCaseSensitiveKeysCreateDifferentResources()
    {
        var service = CreateService();

        PaymentIntentCreateResult lower = await service.CreateAsync(
            ParseKey("order-a"),
            CreateTerms(),
            TestContext.Current.CancellationToken);
        PaymentIntentCreateResult upper = await service.CreateAsync(
            ParseKey("ORDER-A"),
            CreateTerms(),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(lower.Intent!.Id, upper.Intent!.Id);
    }

    [Fact]
    public async Task FindByIdAsync_ReturnsOnlyPublishedIntent()
    {
        var service = CreateService();
        PaymentIntentCreateResult created = await service.CreateAsync(
            ParseKey("lookup"),
            CreateTerms(),
            TestContext.Current.CancellationToken);

        PaymentIntent? found = await service.FindByIdAsync(
            created.Intent!.Id,
            TestContext.Current.CancellationToken);
        PaymentIntent? missing = await service.FindByIdAsync(
            PaymentId.New(),
            TestContext.Current.CancellationToken);

        Assert.Same(created.Intent, found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task CreateAsync_CancellationBeforeMutationPublishesNothing()
    {
        var store = new InMemoryPaymentIntentStore();
        var service = new PaymentIntentService(store, new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            ParseKey("cancelled"),
            CreateTerms(),
            cancellation.Token).AsTask());

        // Reusing the same key must still be a first creation. Querying a
        // random payment ID here would not prove that the key index stayed clean.
        PaymentIntentCreateResult retry = await service.CreateAsync(
            ParseKey("cancelled"),
            CreateTerms(),
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Created, retry.Disposition);
        Assert.NotNull(retry.Intent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains\tcontrol")]
    public void IdempotencyKey_RejectsEmptyWhitespaceOrControlCharacters(string? value)
    {
        Assert.False(IdempotencyKey.TryParse(value, out IdempotencyKey? key));
        Assert.Null(key);
    }

    [Fact]
    public void IdempotencyKey_AcceptsMaximumVisibleAsciiLength()
    {
        Assert.True(IdempotencyKey.TryParse(
            new string('a', IdempotencyKey.MaxLength),
            out IdempotencyKey? key));
        Assert.NotNull(key);

        Assert.False(IdempotencyKey.TryParse(
            new string('a', IdempotencyKey.MaxLength + 1),
            out _));
    }

    private static PaymentIntentService CreateService() =>
        new(new InMemoryPaymentIntentStore(), new FixedTimeProvider(Now));

    private static IdempotencyKey ParseKey(string value)
    {
        Assert.True(IdempotencyKey.TryParse(value, out IdempotencyKey? key));
        return key;
    }

    private static PaymentIntentTerms CreateTerms(
        EvmAddress? token = null,
        RawTokenAmount? amount = null) =>
        new(
            new EvmChainId(31_337),
            token ?? EvmAddress.Parse("0x2222222222222222222222222222222222222222"),
            EvmAddress.Parse("0x3333333333333333333333333333333333333333"),
            amount ?? new RawTokenAmount(1));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
