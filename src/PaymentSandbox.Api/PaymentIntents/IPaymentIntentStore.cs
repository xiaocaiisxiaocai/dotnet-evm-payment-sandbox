using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

public interface IPaymentIntentStore : IPaymentIntentReader
{
    /// <summary>Atomically creates, replays, or rejects one idempotent operation.</summary>
    /// <remarks>
    /// Implementations must compare the key and normalized terms in the same
    /// transaction that publishes both lookup indexes. A separate existence
    /// check followed by an insert does not satisfy this concurrency contract.
    /// A conflict must not return the previously stored intent.
    /// </remarks>
    ValueTask<PaymentIntentCreateResult> CreateOrGetAsync(
        IdempotencyKey idempotencyKey,
        PaymentIntent candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Finds a previously published intent by its public correlation ID.</summary>
    ValueTask<PaymentIntent?> FindByIdAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentIntentCreateResult(
    PaymentIntentCreateDisposition Disposition,
    PaymentIntent? Intent);

public enum PaymentIntentCreateDisposition
{
    Created,
    Replayed,
    Conflict,
}
