namespace PaymentSandbox.Permits.Workflow;

/// <summary>Opaque local identifier for one durable permit workflow.</summary>
public sealed record PermitOperationId
{
    private PermitOperationId(string value) => Value = value;

    public string Value { get; }

    public static PermitOperationId New() =>
        new(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()));

    public static PermitOperationId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 32 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new FormatException(
                "A permit operation ID must be 32 lowercase hexadecimal characters.");
        }

        return new PermitOperationId(value);
    }

    public override string ToString() => Value;
}
