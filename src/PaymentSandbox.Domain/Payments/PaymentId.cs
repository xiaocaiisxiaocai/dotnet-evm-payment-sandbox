using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace PaymentSandbox.Domain.Payments;

/// <summary>
/// A public 32-byte correlation identifier shared by the API, contract event,
/// indexer, and database.
/// </summary>
/// <remarks>
/// A payment ID is not an invoice number, secret, signature, or authorization.
/// Its only job is to correlate observations across system boundaries. Business
/// uniqueness is enforced off-chain; the contract intentionally permits repeated
/// IDs so partial, supplemental, and accidental duplicate payments remain visible.
/// </remarks>
public sealed record PaymentId
{
    private const int ByteLength = 32;
    private const int HexLength = ByteLength * 2;

    private PaymentId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical lowercase, <c>0x</c>-prefixed representation.</summary>
    public string Value { get; }

    /// <summary>Generates a cryptographically random, non-zero payment ID.</summary>
    public static PaymentId New()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];

        // Zero is reserved as an invalid sentinel by PaymentRouter. The retry is
        // practically unreachable, but makes the invariant explicit and complete.
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (IsAllZero(bytes));

        return FromBytes(bytes);
    }

    /// <summary>Parses a 32-byte hexadecimal ID and normalizes its casing.</summary>
    /// <exception cref="FormatException">Thrown for a malformed or all-zero ID.</exception>
    public static PaymentId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out PaymentId? paymentId))
        {
            throw new FormatException(
                "A payment ID must be a non-zero 32-byte hexadecimal value with a 0x prefix.");
        }

        return paymentId;
    }

    /// <summary>Attempts to parse and normalize a hexadecimal payment ID.</summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out PaymentId? paymentId)
    {
        paymentId = null;

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

        if (IsAllZero(bytes))
        {
            return false;
        }

        paymentId = FromBytes(bytes);
        return true;
    }

    /// <summary>Creates an ID from exactly 32 non-zero bytes.</summary>
    public static PaymentId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A payment ID must contain exactly 32 bytes.", nameof(bytes));
        }

        if (IsAllZero(bytes))
        {
            throw new ArgumentException("The all-zero payment ID is reserved and invalid.", nameof(bytes));
        }

        return new PaymentId($"0x{Convert.ToHexString(bytes).ToLowerInvariant()}");
    }

    /// <summary>Returns a new byte array containing the 32-byte identifier.</summary>
    public byte[] ToBytes() => Convert.FromHexString(Value.AsSpan(2));

    /// <summary>Returns the canonical hexadecimal representation.</summary>
    public override string ToString() => Value;

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
