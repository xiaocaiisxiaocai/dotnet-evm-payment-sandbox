namespace PaymentSandbox.Permits.Erc2612;

/// <summary>Stable failure that never retains a supplied wallet signature.</summary>
public sealed class Erc2612PermitException : Exception
{
    internal Erc2612PermitException(Erc2612PermitErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public Erc2612PermitErrorCode Code { get; }
}

public enum Erc2612PermitErrorCode
{
    PolicyMismatch,
    InvalidSignature,
    PermitExpired,
    RouterMismatch,
}
