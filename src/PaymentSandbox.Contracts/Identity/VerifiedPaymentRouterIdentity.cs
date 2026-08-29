using System.Numerics;

namespace PaymentSandbox.Contracts.Identity;

/// <summary>A point-in-time identity observation accepted against a trust policy.</summary>
/// <remarks>
/// This proves that one RPC endpoint reported the expected chain and runtime code.
/// It does not prove that the endpoint is honest, that a block is final, or that
/// later calls see the same state. Independent RPC/finality checks remain later
/// roadmap work.
/// </remarks>
public sealed record VerifiedPaymentRouterIdentity(
    BigInteger ChainId,
    string ContractAddress,
    string RuntimeCodeKeccak256);
