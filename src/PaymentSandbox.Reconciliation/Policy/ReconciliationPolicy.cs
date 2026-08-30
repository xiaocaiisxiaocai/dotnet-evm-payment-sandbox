using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Reconciliation.Policy;

/// <summary>Names one immutable interpretation of reconciliation results.</summary>
public sealed record ReconciliationPolicy
{
    public ReconciliationPolicy(
        EvmChainId chainId,
        EvmAddress router,
        string policyId,
        int maxLedgerEntriesPerPayment = 1_000,
        int maxFinalityTransitionsPerEffect = 100)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        if (policyId.Length > 64 || policyId.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "A policy ID must contain 1-64 ASCII letters, digits, '.', '_' or '-'.",
                nameof(policyId));
        }

        if (maxLedgerEntriesPerPayment is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLedgerEntriesPerPayment));
        }

        if (maxFinalityTransitionsPerEffect is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFinalityTransitionsPerEffect));
        }

        PolicyId = policyId;
        MaxLedgerEntriesPerPayment = maxLedgerEntriesPerPayment;
        MaxFinalityTransitionsPerEffect = maxFinalityTransitionsPerEffect;
        Fingerprint = ComputeFingerprint(chainId, router, policyId);
    }

    public EvmChainId ChainId { get; }
    public EvmAddress Router { get; }
    public string PolicyId { get; }
    public int MaxLedgerEntriesPerPayment { get; }
    public int MaxFinalityTransitionsPerEffect { get; }

    /// <summary>Stable policy meaning; read resource limits are deliberately excluded.</summary>
    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        EvmChainId chainId,
        EvmAddress router,
        string policyId)
    {
        string input = $"payment-sandbox/reconciliation-policy/v1\n{chainId}\n{router.Value}\n{policyId}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
