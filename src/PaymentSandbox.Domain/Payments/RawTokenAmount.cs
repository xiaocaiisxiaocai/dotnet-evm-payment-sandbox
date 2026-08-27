using System.Globalization;
using System.Numerics;

namespace PaymentSandbox.Domain.Payments;

/// <summary>
/// Represents an ERC-20 amount in the token's smallest indivisible unit.
/// </summary>
/// <remarks>
/// This type deliberately knows nothing about a token's display decimals. For
/// example, one USDC is represented as 1,000,000 raw units, while one token with
/// 18 decimals is represented as 1,000,000,000,000,000,000 raw units. Keeping
/// amounts as integers prevents rounding and binary floating-point errors.
/// </remarks>
public readonly record struct RawTokenAmount
{
    /// <summary>The largest value accepted by an EVM <c>uint256</c>.</summary>
    public static readonly BigInteger MaxValue = (BigInteger.One << 256) - BigInteger.One;

    /// <summary>Gets the exact unsigned integer sent to or read from the EVM.</summary>
    public BigInteger Value { get; }

    /// <summary>Creates a raw token amount that fits in an EVM <c>uint256</c>.</summary>
    /// <param name="value">The amount in the token's smallest unit.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is negative or exceeds <c>uint256</c>.
    /// </exception>
    public RawTokenAmount(BigInteger value)
    {
        if (value < BigInteger.Zero || value > MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A raw token amount must fit in an unsigned EVM uint256.");
        }

        Value = value;
    }

    /// <summary>Formats the exact integer without locale-specific separators.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
