using System.Diagnostics.CodeAnalysis;

namespace PaymentSandbox.Domain.Evm;

/// <summary>A canonical 20-byte EVM address.</summary>
/// <remarks>
/// This value type validates only the address shape. It does not prove that an
/// account or contract exists, owns a merchant identity, or is trusted. Those
/// checks belong to the application and chain boundaries that have that context.
/// </remarks>
public sealed record EvmAddress
{
    private const int ByteLength = 20;
    private const int HexLength = ByteLength * 2;

    private EvmAddress(string value, bool isZero)
    {
        Value = value;
        IsZero = isZero;
    }

    /// <summary>Gets the canonical lowercase, <c>0x</c>-prefixed address.</summary>
    public string Value { get; }

    /// <summary>Gets whether all 20 address bytes are zero.</summary>
    public bool IsZero { get; }

    /// <summary>Parses a 20-byte hexadecimal address and normalizes its casing.</summary>
    public static EvmAddress Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out EvmAddress? address))
        {
            throw new FormatException(
                "An EVM address must be a 20-byte hexadecimal value with a 0x prefix.");
        }

        return address;
    }

    /// <summary>Attempts to parse a canonicalizable 20-byte address.</summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out EvmAddress? address)
    {
        address = null;

        if (value is null ||
            value.Length != HexLength + 2 ||
            value[0] != '0' ||
            (value[1] != 'x' && value[1] != 'X'))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value.AsSpan(2));
        }
        catch (FormatException)
        {
            return false;
        }

        bool isZero = bytes.All(current => current == 0);
        address = new EvmAddress(
            $"0x{Convert.ToHexString(bytes).ToLowerInvariant()}",
            isZero);
        return true;
    }

    /// <summary>Returns a new array containing the 20 address bytes.</summary>
    public byte[] ToBytes() => Convert.FromHexString(Value.AsSpan(2));

    public override string ToString() => Value;
}
