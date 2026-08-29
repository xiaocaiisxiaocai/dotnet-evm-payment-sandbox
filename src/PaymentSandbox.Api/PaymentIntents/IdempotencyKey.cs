using System.Diagnostics.CodeAnalysis;

namespace PaymentSandbox.Api.PaymentIntents;

/// <summary>A case-sensitive client key that scopes one create operation.</summary>
public sealed record IdempotencyKey
{
    public const int MaxLength = 128;

    private IdempotencyKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out IdempotencyKey? idempotencyKey)
    {
        idempotencyKey = null;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        // Visible ASCII avoids control characters, whitespace normalization,
        // Unicode confusables, and ambiguous header/log representations.
        if (value.Any(character => character is < '!' or > '~'))
        {
            return false;
        }

        idempotencyKey = new IdempotencyKey(value);
        return true;
    }
}
