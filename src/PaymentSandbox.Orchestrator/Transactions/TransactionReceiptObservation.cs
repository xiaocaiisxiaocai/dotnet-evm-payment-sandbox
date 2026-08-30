using System.Numerics;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Orchestrator.Transactions;

public enum TransactionExecutionStatus
{
    Succeeded,
    Reverted,
}

/// <summary>One mined receipt observation; it is not protocol finality or settlement.</summary>
public sealed record TransactionReceiptObservation
{
    public TransactionReceiptObservation(
        TransactionHash transactionHash,
        TransactionExecutionStatus status,
        long blockNumber,
        TransactionHash blockHash,
        long gasUsed,
        BigInteger effectiveGasPriceWei)
    {
        TransactionHash = transactionHash ?? throw new ArgumentNullException(nameof(transactionHash));
        BlockHash = blockHash ?? throw new ArgumentNullException(nameof(blockHash));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gasUsed);
        if (effectiveGasPriceWei <= BigInteger.Zero ||
            effectiveGasPriceWei > RawTokenAmount.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveGasPriceWei));
        }

        Status = status;
        BlockNumber = blockNumber;
        GasUsed = gasUsed;
        EffectiveGasPriceWei = effectiveGasPriceWei;
    }

    public TransactionHash TransactionHash { get; }
    public TransactionExecutionStatus Status { get; }
    public long BlockNumber { get; }
    public TransactionHash BlockHash { get; }
    public long GasUsed { get; }
    public BigInteger EffectiveGasPriceWei { get; }
}
