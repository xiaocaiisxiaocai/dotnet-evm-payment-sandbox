using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

/// <summary>The JSON contract for creating one off-chain payment intent.</summary>
/// <remarks>
/// Chain ID and raw amount are strings so JavaScript clients cannot round values
/// above 2^53 before the API sees them.
/// </remarks>
public sealed record CreatePaymentIntentRequest(
    string? ChainId,
    string? TokenAddress,
    string? MerchantAddress,
    string? AmountRaw)
{
    public bool TryCreateTerms(
        [NotNullWhen(true)] out PaymentIntentTerms? terms,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!EvmChainId.TryParse(ChainId, out EvmChainId? chainId))
        {
            errors[nameof(ChainId)] =
                ["chainId must be a positive base-10 integer no larger than uint256.max."];
        }

        if (!EvmAddress.TryParse(TokenAddress, out EvmAddress? token) || token.IsZero)
        {
            errors[nameof(TokenAddress)] =
                ["tokenAddress must be a non-zero 20-byte hexadecimal address."];
        }

        if (!EvmAddress.TryParse(MerchantAddress, out EvmAddress? merchant) || merchant.IsZero)
        {
            errors[nameof(MerchantAddress)] =
                ["merchantAddress must be a non-zero 20-byte hexadecimal address."];
        }

        if (!TryParsePositiveRawAmount(AmountRaw, out RawTokenAmount amount))
        {
            errors[nameof(AmountRaw)] =
                ["amountRaw must be a positive base-10 integer no larger than uint256.max."];
        }

        if (errors.Count != 0)
        {
            terms = null;
            return false;
        }

        terms = new PaymentIntentTerms(chainId!, token!, merchant!, amount);
        return true;
    }

    private static bool TryParsePositiveRawAmount(
        string? value,
        out RawTokenAmount amount)
    {
        amount = default;

        if (string.IsNullOrEmpty(value) || value.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        if (!BigInteger.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out BigInteger parsed) ||
            parsed <= BigInteger.Zero ||
            parsed > RawTokenAmount.MaxValue)
        {
            return false;
        }

        amount = new RawTokenAmount(parsed);
        return true;
    }
}
