using System.Numerics;
using Nethereum.Util;

namespace PaymentSandbox.Contracts.Identity;

/// <summary>Verifies chain and deployed runtime code before an adapter is exposed.</summary>
public sealed class PaymentRouterIdentityVerifier
{
    private readonly IPaymentRouterIdentityRpc _rpc;

    public PaymentRouterIdentityVerifier(IPaymentRouterIdentityRpc rpc)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    }

    public async Task<VerifiedPaymentRouterIdentity> VerifyAsync(
        PaymentRouterTrustPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();

        // Ordering is deliberate and fail-closed:
        // 1. Policy construction already validated local operator configuration.
        // 2. Reject the wrong chain before trusting or even requesting address code.
        // 3. Hash the exact runtime bytes returned for the configured address.
        BigInteger observedChainId = await ObserveChainIdAsync(cancellationToken)
            .ConfigureAwait(false);

        if (observedChainId != policy.ExpectedChainId)
        {
            throw new PaymentRouterIdentityException(
                PaymentRouterIdentityFailure.UnexpectedChainId,
                $"Expected chain ID {policy.ExpectedChainId}, but RPC reported {observedChainId}.");
        }

        string code = await ObserveCodeAsync(policy.ContractAddress, cancellationToken)
            .ConfigureAwait(false);
        byte[] runtimeCode = ParseRuntimeCode(code);
        string observedHash = ToCanonicalHash(
            Sha3Keccack.Current.CalculateHash(runtimeCode));

        if (!string.Equals(
                observedHash,
                policy.ExpectedRuntimeCodeKeccak256,
                StringComparison.Ordinal))
        {
            throw new PaymentRouterIdentityException(
                PaymentRouterIdentityFailure.RuntimeCodeHashMismatch,
                $"Runtime code hash mismatch at {policy.ContractAddress}: " +
                $"expected {policy.ExpectedRuntimeCodeKeccak256}, observed {observedHash}.");
        }

        return new VerifiedPaymentRouterIdentity(
            observedChainId,
            policy.ContractAddress,
            observedHash);
    }

    private async Task<BigInteger> ObserveChainIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _rpc.GetChainIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PaymentRouterIdentityException(
                PaymentRouterIdentityFailure.RpcRequestFailed,
                "The RPC chain ID observation failed.",
                exception);
        }
    }

    private async Task<string> ObserveCodeAsync(
        string contractAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _rpc.GetCodeAsync(contractAddress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PaymentRouterIdentityException(
                PaymentRouterIdentityFailure.RpcRequestFailed,
                "The RPC runtime code observation failed.",
                exception);
        }
    }

    private static byte[] ParseRuntimeCode(string? code)
    {
        if (string.IsNullOrEmpty(code) ||
            string.Equals(code, "0x", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentRouterIdentityException(
                PaymentRouterIdentityFailure.CodeMissing,
                "No deployed runtime code exists at the configured Router address.");
        }

        if (code.Length < 4 ||
            code.Length % 2 != 0 ||
            code[0] != '0' ||
            (code[1] != 'x' && code[1] != 'X'))
        {
            throw MalformedCode();
        }

        try
        {
            return Convert.FromHexString(code.AsSpan(2));
        }
        catch (FormatException)
        {
            throw MalformedCode();
        }
    }

    private static PaymentRouterIdentityException MalformedCode() =>
        new(
            PaymentRouterIdentityFailure.CodeMalformed,
            "RPC returned malformed runtime bytecode instead of 0x-prefixed bytes.");

    private static string ToCanonicalHash(byte[] hash) =>
        $"0x{Convert.ToHexString(hash).ToLowerInvariant()}";
}
