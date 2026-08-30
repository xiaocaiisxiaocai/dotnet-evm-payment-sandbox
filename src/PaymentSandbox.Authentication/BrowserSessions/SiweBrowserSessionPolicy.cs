namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>Bounds the lifetime and retained count of opaque browser sessions.</summary>
public sealed record SiweBrowserSessionPolicy
{
    public SiweBrowserSessionPolicy(TimeSpan? sessionLifetime = null)
    {
        SessionLifetime = sessionLifetime ?? TimeSpan.FromMinutes(30);
        if (SessionLifetime < TimeSpan.FromMinutes(5) ||
            SessionLifetime > TimeSpan.FromHours(24) ||
            SessionLifetime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
        }
    }

    public TimeSpan SessionLifetime { get; }
}
