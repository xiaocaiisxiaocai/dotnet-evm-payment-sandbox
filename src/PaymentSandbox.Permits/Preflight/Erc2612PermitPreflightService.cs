using System.Numerics;
using Nethereum.Util;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Erc2612;

namespace PaymentSandbox.Permits.Preflight;

/// <summary>Fails closed unless one exact-block token view matches reviewed policy.</summary>
public sealed class Erc2612PermitPreflightService
{
    private readonly Erc2612TokenTrustPolicy _policy;
    private readonly IErc2612TokenSnapshotRpc _rpc;

    public Erc2612PermitPreflightService(
        Erc2612TokenTrustPolicy policy,
        IErc2612TokenSnapshotRpc rpc)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
    }

    public async Task<VerifiedErc2612TokenSnapshot> ObserveAsync(
        EvmAddress owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsZero)
        {
            throw new ArgumentException("The permit owner cannot be zero.", nameof(owner));
        }

        Erc2612TokenSnapshotObservation observed;
        try
        {
            observed = await _rpc.ObserveAsync(
                _policy.PermitPolicy.Token,
                owner,
                cancellationToken).ConfigureAwait(false)
                ?? throw Error(Erc2612PreflightErrorCode.ObservationFailed,
                    "The token observation returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Erc2612PreflightException)
        {
            throw;
        }
        catch (Exception)
        {
            // RPC errors can contain a credential-bearing endpoint, response,
            // or attacker-controlled contract return. Do not retain it here.
            throw Error(
                Erc2612PreflightErrorCode.ObservationFailed,
                "The ERC-2612 token observation failed.");
        }

        Erc2612PermitPolicy permit = _policy.PermitPolicy;
        if (observed.ChainId != permit.ChainId.Value)
        {
            throw Error(Erc2612PreflightErrorCode.ChainMismatch,
                "The observed token chain does not match permit policy.");
        }

        if (observed.Token != permit.Token || observed.Owner != owner)
        {
            throw Error(Erc2612PreflightErrorCode.TokenMismatch,
                "The token observation identity does not match the request.");
        }

        if (observed.BlockNumber < 0 || observed.Nonce < BigInteger.Zero ||
            observed.Nonce > RawTokenAmount.MaxValue)
        {
            throw Error(Erc2612PreflightErrorCode.InvalidObservation,
                "The token observation contains an invalid block or nonce.");
        }

        string blockHash = RequireCanonicalBytes32(
            observed.BlockHash,
            Erc2612PreflightErrorCode.InvalidObservation,
            "The token observation contains an invalid block hash.");
        string domainSeparator = RequireCanonicalBytes32(
            observed.DomainSeparator,
            Erc2612PreflightErrorCode.InvalidObservation,
            "The token observation contains an invalid domain separator.");
        string runtimeCodeHash = HashRuntimeCode(observed.RuntimeCode);
        if (!string.Equals(runtimeCodeHash, _policy.ExpectedRuntimeCodeHash,
                StringComparison.Ordinal))
        {
            throw Error(Erc2612PreflightErrorCode.RuntimeCodeMismatch,
                "The observed token runtime code does not match reviewed policy.");
        }

        if (!string.Equals(observed.TokenName, permit.TokenName, StringComparison.Ordinal))
        {
            throw Error(Erc2612PreflightErrorCode.TokenNameMismatch,
                "The observed token name does not match permit policy.");
        }

        string expectedDomain = Erc2612PermitService.CalculateDomainSeparator(permit);
        if (!string.Equals(domainSeparator, expectedDomain, StringComparison.Ordinal))
        {
            throw Error(Erc2612PreflightErrorCode.DomainSeparatorMismatch,
                "The observed token domain separator does not match permit policy.");
        }

        return new VerifiedErc2612TokenSnapshot(
            owner,
            observed.Nonce,
            observed.BlockNumber,
            blockHash,
            runtimeCodeHash,
            domainSeparator);
    }

    private static string HashRuntimeCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length <= 2 || value.Length % 2 != 0 ||
            !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(Erc2612PreflightErrorCode.InvalidObservation,
                "The token observation contains invalid runtime code.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value.AsSpan(2));
        }
        catch (FormatException)
        {
            throw Error(Erc2612PreflightErrorCode.InvalidObservation,
                "The token observation contains invalid runtime code.");
        }

        return $"0x{Convert.ToHexStringLower(Sha3Keccack.Current.CalculateHash(bytes))}";
    }

    private static string RequireCanonicalBytes32(
        string value,
        Erc2612PreflightErrorCode code,
        string message)
    {
        try
        {
            return Erc2612TokenTrustPolicy.RequireBytes32(value, nameof(value));
        }
        catch (ArgumentException)
        {
            throw Error(code, message);
        }
    }

    private static Erc2612PreflightException Error(
        Erc2612PreflightErrorCode code,
        string message) => new(code, message);
}
