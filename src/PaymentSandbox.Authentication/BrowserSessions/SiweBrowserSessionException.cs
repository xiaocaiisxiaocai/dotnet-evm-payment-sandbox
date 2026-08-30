namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>A stable session failure that never retains an opaque bearer token.</summary>
public sealed class SiweBrowserSessionException : Exception
{
    internal SiweBrowserSessionException(
        SiweBrowserSessionErrorCode code,
        string message) : base(message)
    {
        Code = code;
    }

    public SiweBrowserSessionErrorCode Code { get; }
}

public enum SiweBrowserSessionErrorCode
{
    InvalidBrowserBinding,
    BrowserFlowExpired,
    BrowserFlowAlreadyUsed,
    SessionNotFound,
    SessionExpired,
    SessionRevoked,
    CsrfMismatch,
    SessionCapacityExceeded,
}
