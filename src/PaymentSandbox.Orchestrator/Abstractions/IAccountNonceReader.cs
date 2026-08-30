using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Orchestrator.Abstractions;

/// <summary>Reads an untrusted RPC pending nonce before local reservation.</summary>
public interface IAccountNonceReader
{
    Task<long> GetPendingNonceAsync(
        EvmChainId chainId,
        EvmAddress account,
        CancellationToken cancellationToken = default);
}
