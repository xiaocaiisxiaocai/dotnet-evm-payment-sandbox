using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.PaymentIntents;

/// <summary>An intent lookup and publication high-watermark read atomically.</summary>
/// <remarks>
/// A missing intent is meaningful only with a publication watermark: a later
/// creation receives a larger ID and therefore cannot change the selected past
/// snapshot.
/// </remarks>
public sealed record PaymentIntentReadSnapshot
{
    public PaymentIntentReadSnapshot(
        PaymentId paymentId,
        PaymentIntent? intent,
        long publicationHighWatermark,
        long? publicationId)
    {
        PaymentId = paymentId ?? throw new ArgumentNullException(nameof(paymentId));
        ArgumentOutOfRangeException.ThrowIfNegative(publicationHighWatermark);
        if (publicationId is <= 0 || publicationId > publicationHighWatermark)
        {
            throw new ArgumentOutOfRangeException(nameof(publicationId));
        }

        if ((intent is null) != (publicationId is null) ||
            (intent is not null && intent.Id != paymentId))
        {
            throw new ArgumentException(
                "Intent and publication identity must describe the requested payment ID.",
                nameof(intent));
        }

        Intent = intent;
        PublicationHighWatermark = publicationHighWatermark;
        PublicationId = publicationId;
    }

    public PaymentId PaymentId { get; }
    public PaymentIntent? Intent { get; }
    public long PublicationHighWatermark { get; }
    public long? PublicationId { get; }
}

/// <summary>Read-only intent boundary for downstream projections.</summary>
public interface IPaymentIntentReader
{
    ValueTask<PaymentIntentReadSnapshot> GetSnapshotAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default);
}
