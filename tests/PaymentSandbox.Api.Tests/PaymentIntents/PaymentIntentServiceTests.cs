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
    public async Task CreateAsync_GeneratesCandidateAndDelegatesStoreDecision()
    {
        var store = new CapturingStore();
        var service = new PaymentIntentService(store, new FixedTimeProvider(Now));
        IdempotencyKey key = ParseKey("checkout-123");
        PaymentIntentTerms terms = CreateTerms();

        PaymentIntentCreateResult result = await service.CreateAsync(
            key,
            terms,
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentIntentCreateDisposition.Created, result.Disposition);
        Assert.Same(key, store.CreatedWithKey);
        Assert.Same(result.Intent, store.CreatedCandidate);
        Assert.Equal(terms, result.Intent!.Terms);
        Assert.Equal(Now, result.Intent.CreatedAtUtc);
    }

    [Fact]
    public async Task FindByIdAsync_DelegatesPublicCorrelationId()
    {
        PaymentIntent expected = PaymentIntent.Create(PaymentId.New(), CreateTerms(), Now);
        var store = new CapturingStore { FoundIntent = expected };
        var service = new PaymentIntentService(store, new FixedTimeProvider(Now));

        PaymentIntent? result = await service.FindByIdAsync(
            expected.Id,
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Same(expected.Id, store.FoundWithId);
    }

    [Fact]
    public async Task CreateAsync_CancellationStopsBeforeStoreMutation()
    {
        var store = new CapturingStore();
        var service = new PaymentIntentService(store, new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            ParseKey("cancelled"),
            CreateTerms(),
            cancellation.Token).AsTask());

        Assert.Null(store.CreatedCandidate);
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

    private static IdempotencyKey ParseKey(string value)
    {
        Assert.True(IdempotencyKey.TryParse(value, out IdempotencyKey? key));
        return key;
    }

    private static PaymentIntentTerms CreateTerms() =>
        new(
            new EvmChainId(31_337),
            EvmAddress.Parse("0x2222222222222222222222222222222222222222"),
            EvmAddress.Parse("0x3333333333333333333333333333333333333333"),
            new RawTokenAmount(1));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CapturingStore : IPaymentIntentStore
    {
        public IdempotencyKey? CreatedWithKey { get; private set; }

        public PaymentIntent? CreatedCandidate { get; private set; }

        public PaymentId? FoundWithId { get; private set; }

        public PaymentIntent? FoundIntent { get; init; }

        public ValueTask<PaymentIntentCreateResult> CreateOrGetAsync(
            IdempotencyKey idempotencyKey,
            PaymentIntent candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedWithKey = idempotencyKey;
            CreatedCandidate = candidate;
            return ValueTask.FromResult(new PaymentIntentCreateResult(
                PaymentIntentCreateDisposition.Created,
                candidate));
        }

        public ValueTask<PaymentIntent?> FindByIdAsync(
            PaymentId paymentId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FoundWithId = paymentId;
            return ValueTask.FromResult(FoundIntent);
        }

        public ValueTask<PaymentIntentReadSnapshot> GetSnapshotAsync(
            PaymentId paymentId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PaymentIntentReadSnapshot(
                paymentId,
                FoundIntent,
                FoundIntent is null ? 0 : 1,
                FoundIntent is null ? null : 1));
        }
    }
}
