using System.Numerics;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>Exact EIP-1559 fee fields proposed for one transaction attempt.</summary>
public sealed record TransactionFeeQuote
{
    public TransactionFeeQuote(BigInteger maxFeePerGasWei, BigInteger maxPriorityFeePerGasWei)
    {
        if (maxFeePerGasWei <= BigInteger.Zero || maxFeePerGasWei > RawTokenAmount.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFeePerGasWei));
        }

        if (maxPriorityFeePerGasWei <= BigInteger.Zero ||
            maxPriorityFeePerGasWei > maxFeePerGasWei)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPriorityFeePerGasWei));
        }

        MaxFeePerGasWei = maxFeePerGasWei;
        MaxPriorityFeePerGasWei = maxPriorityFeePerGasWei;
    }

    public BigInteger MaxFeePerGasWei { get; }
    public BigInteger MaxPriorityFeePerGasWei { get; }
}
