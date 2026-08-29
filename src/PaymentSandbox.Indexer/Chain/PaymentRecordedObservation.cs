using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Indexer.Chain;

/// <summary>One decoded PaymentRecorded log tied to its exact chain occurrence.</summary>
/// <remarks>
/// This is immutable observation evidence. It is not a finality decision, an
/// accounting entry, or proof that an unusual token delivered the event amount.
/// </remarks>
public sealed record PaymentRecordedObservation
{
    public PaymentRecordedObservation(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        EvmHash transactionHash,
        long logIndex,
        PaymentId paymentId,
        EvmAddress payer,
        EvmAddress token,
        EvmAddress merchant,
        RawTokenAmount amount)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = RequireNonZero(router, nameof(router));
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        BlockNumber = blockNumber;
        BlockHash = blockHash ?? throw new ArgumentNullException(nameof(blockHash));
        TransactionHash = transactionHash ?? throw new ArgumentNullException(nameof(transactionHash));
        ArgumentOutOfRangeException.ThrowIfNegative(logIndex);
        LogIndex = logIndex;
        PaymentId = paymentId ?? throw new ArgumentNullException(nameof(paymentId));
        Payer = RequireNonZero(payer, nameof(payer));
        Token = RequireNonZero(token, nameof(token));
        Merchant = RequireNonZero(merchant, nameof(merchant));
        if (amount.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The observed amount must be positive.");
        }

        Amount = amount;
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long BlockNumber { get; }

    public EvmHash BlockHash { get; }

    public EvmHash TransactionHash { get; }

    public long LogIndex { get; }

    public PaymentId PaymentId { get; }

    public EvmAddress Payer { get; }

    public EvmAddress Token { get; }

    public EvmAddress Merchant { get; }

    public RawTokenAmount Amount { get; }

    private static EvmAddress RequireNonZero(EvmAddress? address, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(address, parameterName);
        if (address.IsZero)
        {
            throw new ArgumentException("An observed address cannot be zero.", parameterName);
        }

        return address;
    }
}
