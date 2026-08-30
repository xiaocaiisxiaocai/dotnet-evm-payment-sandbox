using System.Security.Cryptography;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Issues and atomically consumes bounded SIWE challenges.</summary>
public sealed class SiweAuthenticationService
{
    private const int NonceByteLength = 16;
    private const int MaxNonceGenerationAttempts = 5;
    private readonly SiweAuthenticationPolicy _policy;
    private readonly ISiweChallengeStore _store;
    private readonly TimeProvider _timeProvider;

    public SiweAuthenticationService(
        SiweAuthenticationPolicy policy,
        ISiweChallengeStore store,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Creates a server nonce before any wallet address is trusted.</summary>
    public async Task<SiweChallenge> IssueChallengeAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset issuedAt = SiweTime.TruncateToSecond(_timeProvider.GetUtcNow());
        for (int attempt = 0; attempt < MaxNonceGenerationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string nonce = Convert.ToHexStringLower(
                RandomNumberGenerator.GetBytes(NonceByteLength));
            var challenge = new SiweChallenge(
                _policy.Domain,
                _policy.RequestUri,
                _policy.ChainId,
                _policy.Statement,
                nonce,
                issuedAt,
                issuedAt + _policy.ChallengeLifetime,
                _policy.Fingerprint);
            SiweChallengeAddResult added = await _store.TryAddAsync(
                challenge, cancellationToken).ConfigureAwait(false);
            if (added == SiweChallengeAddResult.Added)
            {
                return challenge;
            }

            if (added == SiweChallengeAddResult.CapacityExceeded)
            {
                throw Error(
                    SiweAuthenticationErrorCode.ChallengeCapacityExceeded,
                    "The SIWE challenge store reached its configured capacity.");
            }
        }

        throw Error(
            SiweAuthenticationErrorCode.ChallengeCapacityExceeded,
            "A unique SIWE challenge nonce could not be allocated.");
    }

    /// <summary>
    /// Verifies canonical message facts and ERC-191 recovery, then consumes the
    /// stored nonce in one atomic store call. It does not create a session.
    /// </summary>
    public async Task<SiweAuthenticationResult> AuthenticateAsync(
        string message,
        string signature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SiweMessage parsed = SiweMessageParser.Parse(message);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        _policy.ValidateMessage(parsed, observedAt);

        EvmAddress recovered = SiweEoaSignatureVerifier.Recover(message, signature);
        if (recovered != parsed.Address)
        {
            throw Error(
                SiweAuthenticationErrorCode.InvalidSignature,
                "The ERC-191 signature recovered a different Ethereum address.");
        }

        SiweChallengeConsumeResult consumed = await _store.TryConsumeAsync(
            parsed, _policy.Fingerprint, observedAt, cancellationToken)
            .ConfigureAwait(false);
        return consumed switch
        {
            SiweChallengeConsumeResult.Consumed => new SiweAuthenticationResult(
                recovered, parsed.ChainId, observedAt),
            SiweChallengeConsumeResult.NotFound => throw Error(
                SiweAuthenticationErrorCode.ChallengeNotFound,
                "The SIWE challenge does not exist."),
            SiweChallengeConsumeResult.AlreadyConsumed => throw Error(
                SiweAuthenticationErrorCode.ChallengeAlreadyUsed,
                "The SIWE challenge was already consumed."),
            SiweChallengeConsumeResult.Expired => throw Error(
                SiweAuthenticationErrorCode.ChallengeExpired,
                "The SIWE challenge expired."),
            SiweChallengeConsumeResult.FactsMismatch => throw Error(
                SiweAuthenticationErrorCode.PolicyMismatch,
                "The SIWE message differs from the issued challenge."),
            _ => throw new InvalidOperationException("Unknown SIWE consume result."),
        };
    }

    private static SiweAuthenticationException Error(
        SiweAuthenticationErrorCode code,
        string message) => new(code, message);
}
