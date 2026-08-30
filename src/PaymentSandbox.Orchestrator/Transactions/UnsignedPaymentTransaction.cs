using System.Numerics;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>The complete policy-approved EIP-1559 transaction presented to the signer.</summary>
/// <remarks>
/// Value is always zero because the Router transfers ERC-20 tokens via calldata.
/// This value contains no signature or private key.
/// </remarks>
public sealed record UnsignedPaymentTransaction(
    EvmChainId ChainId,
    EvmAddress Signer,
    EvmAddress Destination,
    long Nonce,
    long GasLimit,
    BigInteger MaxFeePerGasWei,
    BigInteger MaxPriorityFeePerGasWei,
    string Data);
