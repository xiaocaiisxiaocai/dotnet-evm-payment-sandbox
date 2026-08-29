using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.PaymentIntents;

/// <summary>The immutable payment facts supplied when an off-chain intent is created.</summary>
public sealed record PaymentIntentTerms
{
    public PaymentIntentTerms(
        EvmChainId chainId,
        EvmAddress token,
        EvmAddress merchant,
        RawTokenAmount amount)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Merchant = merchant ?? throw new ArgumentNullException(nameof(merchant));

        if (token.IsZero)
        {
            throw new ArgumentException("A payment token cannot be the zero address.", nameof(token));
        }

        if (merchant.IsZero)
        {
            throw new ArgumentException("A payment merchant cannot be the zero address.", nameof(merchant));
        }

        if (amount.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A payment intent amount must be greater than zero.");
        }

        Amount = amount;
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Token { get; }

    public EvmAddress Merchant { get; }

    public RawTokenAmount Amount { get; }
}
