using System.Diagnostics.CodeAnalysis;

namespace PaymentSandbox.Indexer.Chain;

/// <summary>A canonical 32-byte EVM hash used for blocks and transactions.</summary>
/// <remarks>
/// Zero is valid here because a genesis block may report a zero parent hash.
/// This differs from PaymentId, whose all-zero value is deliberately reserved.
/// </remarks>
public sealed record EvmHash
{
    private const int ByteLength = 32;
    private const int TextLength = 2 + (ByteLength * 2);

    private EvmHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static EvmHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out EvmHash? hash))
        {
            throw new FormatException(
                "An EVM hash must be a 32-byte hexadecimal value with a 0x prefix.");
        }

        return hash;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out EvmHash? hash)
    {
        hash = null;
        if (value is null ||
            value.Length != TextLength ||
            value[0] != '0' ||
            (value[1] != 'x' && value[1] != 'X'))
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromHexString(value.AsSpan(2));
            hash = new EvmHash($"0x{Convert.ToHexString(bytes).ToLowerInvariant()}");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public override string ToString() => Value;
}
