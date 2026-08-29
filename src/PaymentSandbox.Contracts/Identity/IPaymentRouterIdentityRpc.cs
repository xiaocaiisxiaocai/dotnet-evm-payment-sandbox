using System.Numerics;

namespace PaymentSandbox.Contracts.Identity;

/// <summary>The two read-only RPC observations needed to identify a Router.</summary>
/// <remarks>
/// Keeping this interface narrow makes the trust boundary visible and lets tests
/// exercise every failure without a network. It intentionally has no account,
/// signing, transaction submission, or receipt-polling operation.
/// </remarks>
public interface IPaymentRouterIdentityRpc
{
    Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default);

    Task<string> GetCodeAsync(
        string contractAddress,
        CancellationToken cancellationToken = default);
}
