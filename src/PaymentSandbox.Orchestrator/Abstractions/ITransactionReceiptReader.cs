using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Abstractions;

/// <summary>Reads one mined receipt observation without claiming finality.</summary>
public interface ITransactionReceiptReader
{
    Task<TransactionReceiptObservation?> GetReceiptAsync(
        EvmChainId chainId,
        TransactionHash transactionHash,
        CancellationToken cancellationToken = default);
}
