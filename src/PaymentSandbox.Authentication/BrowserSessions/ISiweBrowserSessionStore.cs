using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>Durable boundary for browser bindings and opaque SIWE sessions.</summary>
public interface ISiweBrowserSessionStore
{
    /// <summary>Binds one issued nonce to a hash of a separate browser secret.</summary>
    Task<SiweFlowBindResult> TryBindFlowAsync(
        string nonce,
        string bindingTokenHash,
        DateTimeOffset expirationTimeUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Reads binding, expiry, and one-way use state without mutating it.</summary>
    Task<SiweFlowValidationResult> ValidateFlowAsync(
        string nonce,
        string bindingTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes the browser flow, inserts new hashed credentials,
    /// and revokes an optional previous session.
    /// </summary>
    Task<SiweSessionCreateResult> TryCreateSessionAsync(
        string nonce,
        string bindingTokenHash,
        string sessionTokenHash,
        string csrfTokenHash,
        EvmAddress address,
        EvmChainId chainId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expirationTimeUtc,
        string? previousSessionTokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up a session by bearer-token hash at an explicit time.</summary>
    Task<SiweSessionLookup> FindSessionAsync(
        string sessionTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes an active session only when its CSRF-token hash matches.</summary>
    Task<SiweSessionRevokeResult> TryRevokeSessionAsync(
        string sessionTokenHash,
        string csrfTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum SiweFlowBindResult
{
    Bound,
    ChallengeNotFound,
    NonceAlreadyBound,
    DuplicateBindingToken,
}

public enum SiweFlowValidationResult
{
    Valid,
    NotFound,
    BindingMismatch,
    Expired,
    AlreadyConsumed,
}

public enum SiweSessionCreateResult
{
    Created,
    DuplicateSessionToken,
    CapacityExceeded,
    FlowNotFound,
    FlowBindingMismatch,
    FlowExpired,
    FlowAlreadyConsumed,
}

public enum SiweSessionLookupResult
{
    Active,
    NotFound,
    Expired,
    Revoked,
}

public enum SiweSessionRevokeResult
{
    Revoked,
    NotFound,
    Expired,
    AlreadyRevoked,
    CsrfMismatch,
}
