using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace PaymentSandbox.Domain.Evm;

/// <summary>A positive EVM chain identifier represented without JSON precision loss.</summary>
public sealed record EvmChainId
{
    private static readonly BigInteger MaxValue = (BigInteger.One << 256) - BigInteger.One;

    public EvmChainId(BigInteger value)
    {
        if (value <= BigInteger.Zero || value > MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "An EVM chain ID must be between 1 and uint256.max.");
        }

        Value = value;
    }

    public BigInteger Value { get; }

    /// <summary>Parses an unsigned base-10 chain ID.</summary>
    public static EvmChainId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out EvmChainId? chainId))
        {
            throw new FormatException(
                "A chain ID must be a positive base-10 integer no larger than uint256.max.");
        }

        return chainId;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out EvmChainId? chainId)
    {
        chainId = null;

        // NumberStyles.None still permits some culture-specific behavior. The
        // explicit ASCII pass keeps the HTTP/domain representation unambiguous.
        if (string.IsNullOrEmpty(value) || value.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        if (!BigInteger.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out BigInteger parsed) ||
            parsed <= BigInteger.Zero ||
            parsed > MaxValue)
        {
            return false;
        }

        chainId = new EvmChainId(parsed);
        return true;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
