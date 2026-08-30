using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>Explicit test-only request whose Router calldata will be locally encoded.</summary>
public sealed record PaymentTransactionRequest
{
    public PaymentTransactionRequest(
        TransactionOperationId operationId,
        PaymentId paymentId,
        EvmAddress token,
        EvmAddress merchant,
        RawTokenAmount amount,
        long gasLimit,
        TransactionFeeQuote initialFee)
    {
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        PaymentId = paymentId ?? throw new ArgumentNullException(nameof(paymentId));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Merchant = merchant ?? throw new ArgumentNullException(nameof(merchant));
        InitialFee = initialFee ?? throw new ArgumentNullException(nameof(initialFee));
        if (token.IsZero || merchant.IsZero)
        {
            throw new ArgumentException("Token and merchant addresses cannot be zero.");
        }

        if (amount.Value.IsZero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gasLimit);
        Amount = amount;
        GasLimit = gasLimit;
    }

    public TransactionOperationId OperationId { get; }
    public PaymentId PaymentId { get; }
    public EvmAddress Token { get; }
    public EvmAddress Merchant { get; }
    public RawTokenAmount Amount { get; }
    public long GasLimit { get; }
    public TransactionFeeQuote InitialFee { get; }
}
