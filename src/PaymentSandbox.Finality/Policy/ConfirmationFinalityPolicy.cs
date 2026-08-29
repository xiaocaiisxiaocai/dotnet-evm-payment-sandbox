using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Finality.Policy;

/// <summary>A named, immutable confirmation-depth qualification policy.</summary>
/// <remarks>
/// Meeting this policy is a reversible local classification. It is not a claim
/// of protocol finality, economic irreversibility, or permission to settle.
/// </remarks>
public sealed record ConfirmationFinalityPolicy
{
    public ConfirmationFinalityPolicy(
        EvmChainId chainId,
        EvmAddress router,
        string policyId,
        long requiredConfirmations,
        int maxLedgerEntriesPerEvaluation = 10_000,
        int maxEffectsPerEvaluation = 100_000)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        if (policyId.Length > 64 || policyId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "The policy ID must be 1-64 ASCII letters, digits, dots, hyphens, or underscores.",
                nameof(policyId));
        }

        if (requiredConfirmations is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredConfirmations),
                "Required confirmations must be between 1 and 1,000,000.");
        }

        if (maxLedgerEntriesPerEvaluation is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLedgerEntriesPerEvaluation));
        }

        if (maxEffectsPerEvaluation is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEffectsPerEvaluation));
        }

        PolicyId = policyId;
        RequiredConfirmations = requiredConfirmations;
        MaxLedgerEntriesPerEvaluation = maxLedgerEntriesPerEvaluation;
        MaxEffectsPerEvaluation = maxEffectsPerEvaluation;
        Fingerprint = ComputeFingerprint(chainId, router, policyId, requiredConfirmations);
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public string PolicyId { get; }

    public long RequiredConfirmations { get; }

    public int MaxLedgerEntriesPerEvaluation { get; }

    public int MaxEffectsPerEvaluation { get; }

    /// <summary>Stable identity of policy meaning; resource limits are excluded.</summary>
    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        EvmChainId chainId,
        EvmAddress router,
        string policyId,
        long requiredConfirmations)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "payment-sandbox/confirmation-finality-policy/v1");
        AppendString(hash, chainId.ToString());
        AppendString(hash, router.Value);
        AppendString(hash, policyId);
        Span<byte> value = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(value, requiredConfirmations);
        hash.AppendData(value);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
