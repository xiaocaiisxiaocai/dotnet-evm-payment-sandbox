using System.Diagnostics.CodeAnalysis;

namespace PaymentSandbox.Orchestrator.Transactions;

/// <summary>A caller-owned idempotency key for one test transaction lifecycle.</summary>
public sealed record TransactionOperationId
{
    public const int MaxLength = 64;

    private TransactionOperationId(string value) => Value = value;

    public string Value { get; }

    public static TransactionOperationId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out TransactionOperationId? operationId)
            ? operationId
            : throw new FormatException(
                "An operation ID must contain 1-64 ASCII letters, digits, '.', '_', ':' or '-'.");
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out TransactionOperationId? operationId)
    {
        operationId = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength ||
            value.Any(character => character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and
                not '.' and not '_' and not ':' and not '-'))
        {
            return false;
        }

        operationId = new TransactionOperationId(value);
        return true;
    }

    public override string ToString() => Value;
}
