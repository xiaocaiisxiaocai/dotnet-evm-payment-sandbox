using System.Numerics;
using Nethereum.Util;

namespace PaymentSandbox.Contracts.Identity;

/// <summary>Operator-reviewed facts expected for one PaymentRouter deployment.</summary>
public sealed record PaymentRouterTrustPolicy
{
    public PaymentRouterTrustPolicy(
        BigInteger expectedChainId,
        string contractAddress,
        string expectedRuntimeCodeKeccak256)
    {
        if (expectedChainId <= BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedChainId),
                expectedChainId,
                "The expected chain ID must be greater than zero.");
        }

        ExpectedChainId = expectedChainId;
        ContractAddress = NormalizeContractAddress(contractAddress);
        ExpectedRuntimeCodeKeccak256 = NormalizeHash(expectedRuntimeCodeKeccak256);
    }

    public BigInteger ExpectedChainId { get; }

    /// <summary>Canonical lowercase address configured by the operator.</summary>
    public string ContractAddress { get; }

    /// <summary>Canonical lowercase Keccak-256 of expected runtime bytecode.</summary>
    public string ExpectedRuntimeCodeKeccak256 { get; }

    private static string NormalizeContractAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        // Nethereum validates a lowercase 0x prefix. Accept 0X as equivalent
        // input, then return one canonical lowercase representation below.
        string candidate = address.StartsWith("0X", StringComparison.Ordinal)
            ? $"0x{address[2..]}"
            : address;

        AddressUtil addressUtil = AddressUtil.Current;
        if (!addressUtil.IsValidEthereumAddressHexFormat(candidate) ||
            addressUtil.IsZeroAddress(candidate))
        {
            throw new ArgumentException(
                "The Router address must be a non-zero 20-byte hexadecimal address.",
                nameof(address));
        }

        return candidate.ToLowerInvariant();
    }

    private static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (hash.Length != 66 ||
            hash[0] != '0' ||
            (hash[1] != 'x' && hash[1] != 'X'))
        {
            throw new ArgumentException(
                "The runtime code hash must be a 0x-prefixed 32-byte hexadecimal value.",
                nameof(hash));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hash.AsSpan(2));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The runtime code hash must contain only hexadecimal characters.",
                nameof(hash),
                exception);
        }

        if (bytes.All(value => value == 0))
        {
            throw new ArgumentException(
                "The all-zero runtime code hash is not a valid reviewed identity.",
                nameof(hash));
        }

        return $"0x{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
