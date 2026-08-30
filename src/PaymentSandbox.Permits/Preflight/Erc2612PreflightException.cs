namespace PaymentSandbox.Permits.Preflight;

/// <summary>Sanitized token-observation failure.</summary>
public sealed class Erc2612PreflightException : Exception
{
    internal Erc2612PreflightException(Erc2612PreflightErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public Erc2612PreflightErrorCode Code { get; }
}

public enum Erc2612PreflightErrorCode
{
    ObservationFailed,
    ChainMismatch,
    TokenMismatch,
    RuntimeCodeMismatch,
    TokenNameMismatch,
    DomainSeparatorMismatch,
    InvalidObservation,
}
