using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Policy;

/// <summary>Names and bounds one test-only transaction signing policy.</summary>
public sealed record TransactionLifecyclePolicy
{
    public static readonly BigInteger LocalAnvilChainId = new(31_337);
    public static readonly BigInteger SepoliaChainId = new(11_155_111);

    public TransactionLifecyclePolicy(
        EvmChainId chainId,
        EvmAddress router,
        EvmAddress signer,
        string policyId,
        long maxGasLimit = 1_000_000,
        BigInteger? maxFeePerGasWei = null,
        BigInteger? maxPriorityFeePerGasWei = null,
        int minimumReplacementFeeBumpBasisPoints = 1_000,
        int maxAttemptsPerOperation = 10,
        int maxReservedNonceLead = 100)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = RequireNonZero(router, nameof(router));
        Signer = RequireNonZero(signer, nameof(signer));
        // This project has only two reviewed signing environments. A negative
        // "not mainnet" check would still allow every other public network.
        if (chainId.Value != LocalAnvilChainId && chainId.Value != SepoliaChainId)
        {
            throw new ArgumentException(
                "The test-only orchestrator allows only local Anvil (31337) or Sepolia (11155111).",
                nameof(chainId));
        }

        ValidatePolicyId(policyId);
        if (maxGasLimit is < 21_000 or > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGasLimit));
        }

        MaxFeePerGasWei = maxFeePerGasWei ?? new BigInteger(1_000_000_000_000);
        MaxPriorityFeePerGasWei = maxPriorityFeePerGasWei ?? new BigInteger(100_000_000_000);
        if (MaxFeePerGasWei <= BigInteger.Zero || MaxFeePerGasWei > RawTokenAmount.MaxValue ||
            MaxPriorityFeePerGasWei <= BigInteger.Zero ||
            MaxPriorityFeePerGasWei > RawTokenAmount.MaxValue ||
            MaxPriorityFeePerGasWei > MaxFeePerGasWei)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFeePerGasWei));
        }

        if (minimumReplacementFeeBumpBasisPoints is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumReplacementFeeBumpBasisPoints));
        }

        if (maxAttemptsPerOperation is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttemptsPerOperation));
        }

        if (maxReservedNonceLead is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReservedNonceLead));
        }

        PolicyId = policyId;
        MaxGasLimit = maxGasLimit;
        MinimumReplacementFeeBumpBasisPoints = minimumReplacementFeeBumpBasisPoints;
        MaxAttemptsPerOperation = maxAttemptsPerOperation;
        MaxReservedNonceLead = maxReservedNonceLead;
        Fingerprint = ComputeFingerprint(this);
    }

    public EvmChainId ChainId { get; }
    public EvmAddress Router { get; }
    public EvmAddress Signer { get; }
    public string PolicyId { get; }
    public long MaxGasLimit { get; }
    public BigInteger MaxFeePerGasWei { get; }
    public BigInteger MaxPriorityFeePerGasWei { get; }
    public int MinimumReplacementFeeBumpBasisPoints { get; }
    public int MaxAttemptsPerOperation { get; }
    public int MaxReservedNonceLead { get; }

    /// <summary>Stable signing and lifecycle meaning, including all hard limits.</summary>
    public string Fingerprint { get; }

    public void ValidateInitialRequest(PaymentTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Merchant == Router)
        {
            throw new ArgumentException("The Router cannot be the merchant.", nameof(request));
        }

        if (request.GasLimit > MaxGasLimit)
        {
            throw new ArgumentException("The gas limit exceeds the signing policy.", nameof(request));
        }

        ValidateFeeCap(request.InitialFee, nameof(request));
    }

    public void ValidateReplacement(TransactionFeeQuote previous, TransactionFeeQuote replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateFeeCap(replacement, nameof(replacement));
        if (!MeetsBump(previous.MaxFeePerGasWei, replacement.MaxFeePerGasWei) ||
            !MeetsBump(previous.MaxPriorityFeePerGasWei, replacement.MaxPriorityFeePerGasWei))
        {
            throw new ArgumentException(
                $"Both replacement fee fields must increase by at least {MinimumReplacementFeeBumpBasisPoints} basis points.",
                nameof(replacement));
        }
    }

    private bool MeetsBump(BigInteger previous, BigInteger replacement)
    {
        BigInteger numerator = previous * (10_000 + MinimumReplacementFeeBumpBasisPoints);
        BigInteger minimum = BigInteger.DivRem(numerator, 10_000, out BigInteger remainder);
        if (!remainder.IsZero)
        {
            minimum += BigInteger.One;
        }

        return replacement >= minimum;
    }

    private void ValidateFeeCap(TransactionFeeQuote fee, string parameterName)
    {
        if (fee.MaxFeePerGasWei > MaxFeePerGasWei ||
            fee.MaxPriorityFeePerGasWei > MaxPriorityFeePerGasWei)
        {
            throw new ArgumentException("The fee quote exceeds the signing policy.", parameterName);
        }
    }

    private static EvmAddress RequireNonZero(EvmAddress value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value.IsZero
            ? throw new ArgumentException("The address cannot be zero.", parameterName)
            : value;
    }

    private static void ValidatePolicyId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and
                not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "A policy ID must contain 1-64 ASCII letters, digits, '.', '_' or '-'.",
                nameof(value));
        }
    }

    private static string ComputeFingerprint(TransactionLifecyclePolicy value)
    {
        string input = string.Join('\n',
            "payment-sandbox/transaction-lifecycle-policy/v1",
            value.ChainId.ToString(), value.Router.Value, value.Signer.Value,
            value.PolicyId, value.MaxGasLimit.ToString(),
            value.MaxFeePerGasWei.ToString(), value.MaxPriorityFeePerGasWei.ToString(),
            value.MinimumReplacementFeeBumpBasisPoints.ToString(),
            value.MaxAttemptsPerOperation.ToString(), value.MaxReservedNonceLead.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
