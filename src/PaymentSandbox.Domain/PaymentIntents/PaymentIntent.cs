using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.PaymentIntents;

/// <summary>An immutable off-chain request to receive a payment.</summary>
/// <remarks>
/// <see cref="PaymentIntentStatus.Created"/> means only that this application
/// accepted and stored the request. It does not mean a wallet signed, a node
/// accepted a transaction, a log was observed, or funds reached finality.
/// </remarks>
public sealed record PaymentIntent
{
    private PaymentIntent(
        PaymentId id,
        PaymentIntentTerms terms,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Terms = terms;
        CreatedAtUtc = createdAtUtc;
    }

    public PaymentId Id { get; }

    public PaymentIntentTerms Terms { get; }

    public PaymentIntentStatus Status => PaymentIntentStatus.Created;

    public DateTimeOffset CreatedAtUtc { get; }

    public static PaymentIntent Create(
        PaymentId id,
        PaymentIntentTerms terms,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(terms);

        return new PaymentIntent(id, terms, createdAt.ToUniversalTime());
    }
}

public enum PaymentIntentStatus
{
    /// <summary>The off-chain request exists; no chain outcome is implied.</summary>
    Created,
}
