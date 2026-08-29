using PaymentSandbox.Domain.PaymentIntents;

namespace PaymentSandbox.Api.PaymentIntents;

public sealed record PaymentIntentResponse(
    string PaymentId,
    string Status,
    string ChainId,
    string TokenAddress,
    string MerchantAddress,
    string AmountRaw,
    DateTimeOffset CreatedAtUtc)
{
    public static PaymentIntentResponse FromDomain(PaymentIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return new PaymentIntentResponse(
            intent.Id.Value,
            MapStatus(intent.Status),
            intent.Terms.ChainId.ToString(),
            intent.Terms.Token.Value,
            intent.Terms.Merchant.Value,
            intent.Terms.Amount.ToString(),
            intent.CreatedAtUtc);
    }

    private static string MapStatus(PaymentIntentStatus status) => status switch
    {
        PaymentIntentStatus.Created => "created",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown intent status."),
    };
}
