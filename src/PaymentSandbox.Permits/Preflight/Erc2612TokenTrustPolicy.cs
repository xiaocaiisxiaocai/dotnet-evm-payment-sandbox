using PaymentSandbox.Permits.Erc2612;

namespace PaymentSandbox.Permits.Preflight;

/// <summary>Adds reviewed token runtime identity to the Week 18 permit policy.</summary>
public sealed record Erc2612TokenTrustPolicy
{
    public Erc2612TokenTrustPolicy(
        Erc2612PermitPolicy permitPolicy,
        string expectedRuntimeCodeHash)
    {
        PermitPolicy = permitPolicy ?? throw new ArgumentNullException(nameof(permitPolicy));
        ExpectedRuntimeCodeHash = RequireBytes32(
            expectedRuntimeCodeHash,
            nameof(expectedRuntimeCodeHash));
    }

    public Erc2612PermitPolicy PermitPolicy { get; }
    public string ExpectedRuntimeCodeHash { get; }

    internal static string RequireBytes32(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 66 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !value.AsSpan(2).ToString().All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The value must be one canonical lowercase bytes32 hex string.",
                parameterName);
        }

        string canonical = value.ToLowerInvariant();
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be one canonical lowercase bytes32 hex string.",
                parameterName);
        }

        return canonical;
    }
}
