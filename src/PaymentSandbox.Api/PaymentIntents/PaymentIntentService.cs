using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

/// <summary>Application service for creating and reading off-chain intents.</summary>
public sealed class PaymentIntentService
{
    private readonly IPaymentIntentStore _store;
    private readonly TimeProvider _timeProvider;

    public PaymentIntentService(IPaymentIntentStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<PaymentIntentCreateResult> CreateAsync(
        IdempotencyKey idempotencyKey,
        PaymentIntentTerms terms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(terms);
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = PaymentIntent.Create(
            PaymentId.New(),
            terms,
            _timeProvider.GetUtcNow());

        // The store compares normalized Terms and atomically decides whether
        // this candidate is new, a safe replay, or a conflicting key reuse.
        return _store.CreateOrGetAsync(idempotencyKey, candidate, cancellationToken);
    }

    public ValueTask<PaymentIntent?> FindByIdAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default) =>
        _store.FindByIdAsync(paymentId, cancellationToken);
}
