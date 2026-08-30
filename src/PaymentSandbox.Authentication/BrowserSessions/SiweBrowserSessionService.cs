using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>Composes one SIWE proof into bounded opaque browser credentials.</summary>
public sealed class SiweBrowserSessionService
{
    private const int TokenByteLength = 32;
    private const int MaxTokenGenerationAttempts = 5;
    private readonly SiweAuthenticationService _authentication;
    private readonly ISiweBrowserSessionStore _store;
    private readonly SiweBrowserSessionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public SiweBrowserSessionService(
        SiweAuthenticationService authentication,
        ISiweBrowserSessionStore store,
        SiweBrowserSessionPolicy policy,
        TimeProvider timeProvider)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Issues the signed message and separately binds its nonce to one secret
    /// browser cookie. The supplied address is displayed, not trusted yet.
    /// </summary>
    public async Task<SiweLoginChallenge> IssueAsync(
        EvmAddress requestedAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedAddress);
        if (requestedAddress.IsZero)
        {
            throw new ArgumentException("A login address cannot be zero.", nameof(requestedAddress));
        }

        SiweChallenge challenge = await _authentication.IssueChallengeAsync(cancellationToken)
            .ConfigureAwait(false);
        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            string bindingToken = GenerateToken();
            SiweFlowBindResult bound = await _store.TryBindFlowAsync(
                challenge.Nonce,
                HashToken(bindingToken),
                challenge.ExpirationTimeUtc,
                cancellationToken).ConfigureAwait(false);
            if (bound == SiweFlowBindResult.Bound)
            {
                return new SiweLoginChallenge(
                    challenge.CreateMessage(requestedAddress),
                    bindingToken,
                    challenge.ExpirationTimeUtc);
            }

            if (bound != SiweFlowBindResult.DuplicateBindingToken)
            {
                throw new InvalidOperationException(
                    "The issued SIWE challenge could not be bound to its browser flow.");
            }
        }

        throw Error(
            SiweBrowserSessionErrorCode.SessionCapacityExceeded,
            "A unique browser binding token could not be allocated.");
    }

    /// <summary>
    /// Checks browser binding before consuming the SIWE challenge, then creates
    /// fresh opaque credentials. A previous session token is revoked atomically
    /// with creation, providing login-time session rotation.
    /// </summary>
    public async Task<SiweSessionCredentials> VerifyAsync(
        string message,
        string signature,
        string browserBindingToken,
        string? previousSessionToken = null,
        CancellationToken cancellationToken = default)
    {
        SiweMessage parsed = SiweMessageParser.Parse(message);
        if (!TryHashToken(browserBindingToken, out string? bindingHash))
        {
            throw Error(
                SiweBrowserSessionErrorCode.InvalidBrowserBinding,
                "The browser login binding is invalid.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        SiweFlowValidationResult flow = await _store.ValidateFlowAsync(
            parsed.Nonce, bindingHash, now, cancellationToken).ConfigureAwait(false);
        ThrowUnlessValid(flow);

        // Challenge consumption and session creation intentionally live in two
        // independently useful abstractions. A crash or store failure between
        // them fails safe: no session is issued, but this nonce cannot be reused.
        SiweAuthenticationResult authentication = await _authentication.AuthenticateAsync(
            message, signature, cancellationToken).ConfigureAwait(false);
        DateTimeOffset createdAt = SiweTime.TruncateToSecond(now);
        DateTimeOffset expiresAt = createdAt + _policy.SessionLifetime;
        string? previousHash = TryHashToken(previousSessionToken, out string? hashedPrevious)
            ? hashedPrevious
            : null;

        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            string sessionToken = GenerateToken();
            string csrfToken = GenerateToken();
            SiweSessionCreateResult created = await _store.TryCreateSessionAsync(
                parsed.Nonce,
                bindingHash,
                HashToken(sessionToken),
                HashToken(csrfToken),
                authentication.Address,
                authentication.ChainId,
                createdAt,
                expiresAt,
                previousHash,
                cancellationToken).ConfigureAwait(false);
            if (created == SiweSessionCreateResult.Created)
            {
                return new SiweSessionCredentials(
                    new SiweBrowserSession(
                        authentication.Address,
                        authentication.ChainId,
                        createdAt,
                        expiresAt),
                    sessionToken,
                    csrfToken);
            }

            if (created != SiweSessionCreateResult.DuplicateSessionToken)
            {
                ThrowCreateFailure(created);
            }
        }

        throw Error(
            SiweBrowserSessionErrorCode.SessionCapacityExceeded,
            "Unique browser session credentials could not be allocated.");
    }

    public async Task<SiweBrowserSession> GetSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashToken(sessionToken, out string? sessionHash))
        {
            throw Error(
                SiweBrowserSessionErrorCode.SessionNotFound,
                "The browser session does not exist.");
        }

        SiweSessionLookup lookup = await _store.FindSessionAsync(
            sessionHash,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return lookup.Result switch
        {
            SiweSessionLookupResult.Active => lookup.Session
                ?? throw new InvalidOperationException("An active session lookup returned no session."),
            SiweSessionLookupResult.Expired => throw Error(
                SiweBrowserSessionErrorCode.SessionExpired,
                "The browser session expired."),
            SiweSessionLookupResult.Revoked => throw Error(
                SiweBrowserSessionErrorCode.SessionRevoked,
                "The browser session was revoked."),
            _ => throw Error(
                SiweBrowserSessionErrorCode.SessionNotFound,
                "The browser session does not exist."),
        };
    }

    public async Task LogoutAsync(
        string sessionToken,
        string csrfCookieToken,
        string csrfHeaderToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashToken(sessionToken, out string? sessionHash))
        {
            throw Error(
                SiweBrowserSessionErrorCode.SessionNotFound,
                "The browser session does not exist.");
        }

        if (!TryHashToken(csrfCookieToken, out string? csrfHash) ||
            !IsSameToken(csrfCookieToken, csrfHeaderToken))
        {
            throw Error(
                SiweBrowserSessionErrorCode.CsrfMismatch,
                "The CSRF proof does not match this browser session.");
        }

        SiweSessionRevokeResult revoked = await _store.TryRevokeSessionAsync(
            sessionHash,
            csrfHash,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        switch (revoked)
        {
            case SiweSessionRevokeResult.Revoked:
                return;
            case SiweSessionRevokeResult.CsrfMismatch:
                throw Error(
                    SiweBrowserSessionErrorCode.CsrfMismatch,
                    "The CSRF proof does not match this browser session.");
            case SiweSessionRevokeResult.Expired:
                throw Error(
                    SiweBrowserSessionErrorCode.SessionExpired,
                    "The browser session expired.");
            case SiweSessionRevokeResult.AlreadyRevoked:
                throw Error(
                    SiweBrowserSessionErrorCode.SessionRevoked,
                    "The browser session was already revoked.");
            default:
                throw Error(
                    SiweBrowserSessionErrorCode.SessionNotFound,
                    "The browser session does not exist.");
        }
    }

    public static bool IsCanonicalOpaqueToken(string? value) =>
        value is { Length: TokenByteLength * 2 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string GenerateToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenByteLength));

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private static bool TryHashToken(
        string? value,
        [NotNullWhen(true)] out string? hash)
    {
        if (!IsCanonicalOpaqueToken(value))
        {
            hash = null;
            return false;
        }

        hash = HashToken(value!);
        return true;
    }

    private static bool IsSameToken(string first, string second)
    {
        if (!IsCanonicalOpaqueToken(second))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(first),
            Encoding.ASCII.GetBytes(second));
    }

    private static void ThrowUnlessValid(SiweFlowValidationResult result)
    {
        switch (result)
        {
            case SiweFlowValidationResult.Valid:
                return;
            case SiweFlowValidationResult.Expired:
                throw Error(
                    SiweBrowserSessionErrorCode.BrowserFlowExpired,
                    "The browser login flow expired.");
            case SiweFlowValidationResult.AlreadyConsumed:
                throw Error(
                    SiweBrowserSessionErrorCode.BrowserFlowAlreadyUsed,
                    "The browser login flow was already used.");
            default:
                throw Error(
                    SiweBrowserSessionErrorCode.InvalidBrowserBinding,
                    "The browser login binding is invalid.");
        }
    }

    private static void ThrowCreateFailure(SiweSessionCreateResult result)
    {
        switch (result)
        {
            case SiweSessionCreateResult.CapacityExceeded:
                throw Error(
                    SiweBrowserSessionErrorCode.SessionCapacityExceeded,
                    "The browser session store reached its configured capacity.");
            case SiweSessionCreateResult.FlowExpired:
                throw Error(
                    SiweBrowserSessionErrorCode.BrowserFlowExpired,
                    "The browser login flow expired.");
            case SiweSessionCreateResult.FlowAlreadyConsumed:
                throw Error(
                    SiweBrowserSessionErrorCode.BrowserFlowAlreadyUsed,
                    "The browser login flow was already used.");
            default:
                throw Error(
                    SiweBrowserSessionErrorCode.InvalidBrowserBinding,
                    "The browser login binding is invalid.");
        }
    }

    private static SiweBrowserSessionException Error(
        SiweBrowserSessionErrorCode code,
        string message) => new(code, message);
}
