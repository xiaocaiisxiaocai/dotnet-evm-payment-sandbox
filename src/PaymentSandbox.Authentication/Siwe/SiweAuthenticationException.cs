namespace PaymentSandbox.Authentication.Siwe;

/// <summary>A fail-closed SIWE error that never retains a message or signature.</summary>
public sealed class SiweAuthenticationException : Exception
{
    internal SiweAuthenticationException(
        SiweAuthenticationErrorCode code,
        string message) : base(message)
    {
        Code = code;
    }

    public SiweAuthenticationErrorCode Code { get; }
}
