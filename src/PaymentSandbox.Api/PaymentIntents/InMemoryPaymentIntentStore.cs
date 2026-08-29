using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

/// <summary>A process-local, concurrency-safe Week 6 intent store.</summary>
/// <remarks>
/// One lock protects both indexes so idempotency-key and payment-ID publication
/// is atomic. This store is intentionally not durable or multi-instance safe;
/// replacing it with a database requires an equivalent unique constraint and
/// transaction, not a check-then-insert sequence in application code.
/// </remarks>
public sealed class InMemoryPaymentIntentStore : IPaymentIntentStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PaymentIntent> _byIdempotencyKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaymentIntent> _byPaymentId =
        new(StringComparer.Ordinal);

    public ValueTask<PaymentIntentCreateResult> CreateOrGetAsync(
        IdempotencyKey idempotencyKey,
        PaymentIntent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey.Value, out PaymentIntent? existing))
            {
                if (existing.Terms == candidate.Terms)
                {
                    return ValueTask.FromResult(new PaymentIntentCreateResult(
                        PaymentIntentCreateDisposition.Replayed,
                        existing));
                }

                // Do not return the existing intent on conflict. A caller that
                // guesses another client's key must not learn that resource.
                return ValueTask.FromResult(new PaymentIntentCreateResult(
                    PaymentIntentCreateDisposition.Conflict,
                    Intent: null));
            }

            if (!_byPaymentId.TryAdd(candidate.Id.Value, candidate))
            {
                // A cryptographic ID collision is practically unreachable, but
                // publishing an inconsistent pair of indexes would be worse.
                throw new InvalidOperationException("A generated payment ID already exists.");
            }

            _byIdempotencyKey.Add(idempotencyKey.Value, candidate);
            return ValueTask.FromResult(new PaymentIntentCreateResult(
                PaymentIntentCreateDisposition.Created,
                candidate));
        }
    }

    public ValueTask<PaymentIntent?> FindByIdAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _byPaymentId.TryGetValue(paymentId.Value, out PaymentIntent? intent);
            return ValueTask.FromResult(intent);
        }
    }
}
