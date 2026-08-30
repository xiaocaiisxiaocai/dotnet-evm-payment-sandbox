using System.Diagnostics;
using Nethereum.Util;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>Sensitive signed bytes retained only so an unknown broadcast can replay exactly.</summary>
/// <remarks>
/// Never place <see cref="RawTransaction"/> in logs, exception messages, telemetry,
/// or API responses. The local test database is unencrypted and is not a key vault.
/// </remarks>
[DebuggerDisplay("Signed transaction {TransactionHash.Value} ({ByteLength} bytes; raw redacted)")]
public sealed class SignedTransactionPayload
{
    public const int MaxByteLength = 16 * 1024;

    public SignedTransactionPayload(string rawTransaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTransaction);
        if (rawTransaction.Length < 4 || rawTransaction.Length % 2 != 0 ||
            rawTransaction[0] != '0' ||
            (rawTransaction[1] != 'x' && rawTransaction[1] != 'X'))
        {
            throw new ArgumentException("Signed transaction bytes must be 0x-prefixed hexadecimal.", nameof(rawTransaction));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(rawTransaction.AsSpan(2));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Signed transaction bytes must be 0x-prefixed hexadecimal.",
                nameof(rawTransaction), exception);
        }

        if (bytes.Length is 0 or > MaxByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(rawTransaction));
        }

        RawTransaction = $"0x{Convert.ToHexStringLower(bytes)}";
        ByteLength = bytes.Length;
        TransactionHash = TransactionHash.Parse(
            $"0x{Convert.ToHexStringLower(Sha3Keccack.Current.CalculateHash(bytes))}");
    }

    public string RawTransaction { get; }
    public int ByteLength { get; }
    public TransactionHash TransactionHash { get; }

    public override string ToString() => $"Signed transaction {TransactionHash.Value} (raw redacted)";
}
