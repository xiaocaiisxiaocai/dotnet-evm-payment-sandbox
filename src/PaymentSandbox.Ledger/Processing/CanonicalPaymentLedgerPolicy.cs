using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Ledger.Processing;

/// <summary>Identity and resource limits for one provisional ledger stream.</summary>
public sealed record CanonicalPaymentLedgerPolicy
{
    public CanonicalPaymentLedgerPolicy(
        EvmChainId chainId,
        EvmAddress router,
        int maxTransitionsPerBatch = 1_000,
        int maxPaymentsPerBatch = 10_000)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        if (maxTransitionsPerBatch is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTransitionsPerBatch),
                "The transition limit must be between 1 and 10,000.");
        }

        if (maxPaymentsPerBatch is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPaymentsPerBatch),
                "The payment limit must be between 1 and 100,000.");
        }

        MaxTransitionsPerBatch = maxTransitionsPerBatch;
        MaxPaymentsPerBatch = maxPaymentsPerBatch;
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public int MaxTransitionsPerBatch { get; }

    public int MaxPaymentsPerBatch { get; }
}
