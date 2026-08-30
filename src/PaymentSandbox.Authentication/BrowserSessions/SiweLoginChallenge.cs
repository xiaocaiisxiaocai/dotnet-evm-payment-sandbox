namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>Challenge response plus the secret that must travel only in an HttpOnly cookie.</summary>
public sealed record SiweLoginChallenge(
    string Message,
    string BrowserBindingToken,
    DateTimeOffset ExpirationTimeUtc)
{
    public override string ToString() =>
        $"SIWE login challenge expiring {ExpirationTimeUtc:O} (message and binding token redacted)";
}

/// <summary>New opaque credentials returned only to the HTTP cookie boundary.</summary>
public sealed record SiweSessionCredentials(
    SiweBrowserSession Session,
    string SessionToken,
    string CsrfToken)
{
    public override string ToString() =>
        $"SIWE browser session for {Session.Address.Value} (tokens redacted)";
}
