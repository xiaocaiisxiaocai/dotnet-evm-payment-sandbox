using System.Diagnostics.CodeAnalysis;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>A canonical Keccak-256 identity for one signed transaction payload.</summary>
public sealed record TransactionHash
{
    private TransactionHash(string value) => Value = value;

    public string Value { get; }

    public static TransactionHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out TransactionHash? hash)
            ? hash
            : throw new FormatException(
                "A transaction hash must be a 0x-prefixed non-zero 32-byte hexadecimal value.");
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out TransactionHash? hash)
    {
        hash = null;
        if (value is null || value.Length != 66 || value[0] != '0' ||
            (value[1] != 'x' && value[1] != 'X'))
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromHexString(value.AsSpan(2));
            if (bytes.All(item => item == 0))
            {
                return false;
            }

            hash = new TransactionHash($"0x{Convert.ToHexStringLower(bytes)}");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public override string ToString() => Value;
}
