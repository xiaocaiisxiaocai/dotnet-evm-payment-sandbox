namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Stable, non-sensitive reason codes for the bounded SIWE workflow.</summary>
public enum SiweAuthenticationErrorCode
{
    MalformedMessage,
    PolicyMismatch,
    InvalidSignature,
    ChallengeNotFound,
    ChallengeAlreadyUsed,
    ChallengeExpired,
    ChallengeCapacityExceeded,
}
