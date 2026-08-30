namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Atomic one-time storage boundary for server-issued SIWE nonces.</summary>
public interface ISiweChallengeStore
{
    Task<SiweChallengeAddResult> TryAddAsync(
        SiweChallenge challenge,
        CancellationToken cancellationToken = default);

    Task<SiweChallengeConsumeResult> TryConsumeAsync(
        SiweMessage message,
        string policyFingerprint,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum SiweChallengeAddResult
{
    Added,
    DuplicateNonce,
    CapacityExceeded,
}

public enum SiweChallengeConsumeResult
{
    Consumed,
    NotFound,
    AlreadyConsumed,
    Expired,
    FactsMismatch,
}
