namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Process-local atomic challenge storage for Week 15 tests and learning.</summary>
/// <remarks>
/// This store is intentionally not durable and cannot coordinate multiple
/// processes. It retains used challenges until capacity cleanup so immediate
/// replay has an explainable result. Any replay remains rejected after cleanup,
/// but may be reported as not found rather than already used.
/// </remarks>
public sealed class InMemorySiweChallengeStore : ISiweChallengeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemorySiweChallengeStore(int capacity = 1_024)
    {
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public Task<SiweChallengeAddResult> TryAddAsync(
        SiweChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.ContainsKey(challenge.Nonce))
            {
                return Task.FromResult(SiweChallengeAddResult.DuplicateNonce);
            }

            if (_entries.Count >= _capacity)
            {
                // Cleanup happens only under the same lock as insertion. No
                // concurrent verifier can observe a partly pruned store.
                foreach (string nonce in _entries
                    .Where(pair => pair.Value.Consumed ||
                        pair.Value.Challenge.ExpirationTimeUtc <= challenge.IssuedAtUtc)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _entries.Remove(nonce);
                }
            }

            if (_entries.Count >= _capacity)
            {
                return Task.FromResult(SiweChallengeAddResult.CapacityExceeded);
            }

            _entries.Add(challenge.Nonce, new Entry(challenge));
            return Task.FromResult(SiweChallengeAddResult.Added);
        }
    }

    public Task<SiweChallengeConsumeResult> TryConsumeAsync(
        SiweMessage message,
        string policyFingerprint,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFingerprint);
        cancellationToken.ThrowIfCancellationRequested();
        observedAtUtc = observedAtUtc.ToUniversalTime();
        lock (_gate)
        {
            if (!_entries.TryGetValue(message.Nonce, out Entry? entry))
            {
                return Task.FromResult(SiweChallengeConsumeResult.NotFound);
            }

            if (entry.Consumed)
            {
                return Task.FromResult(SiweChallengeConsumeResult.AlreadyConsumed);
            }

            // "Expiration Time" is an exclusive upper bound: the verifier
            // must still be strictly before it, not equal to it.
            if (observedAtUtc >= entry.Challenge.ExpirationTimeUtc)
            {
                return Task.FromResult(SiweChallengeConsumeResult.Expired);
            }

            if (!string.Equals(
                    entry.Challenge.PolicyFingerprint,
                    policyFingerprint,
                    StringComparison.Ordinal) ||
                !entry.Challenge.Matches(message))
            {
                return Task.FromResult(SiweChallengeConsumeResult.FactsMismatch);
            }

            entry.Consumed = true;
            return Task.FromResult(SiweChallengeConsumeResult.Consumed);
        }
    }

    private sealed class Entry(SiweChallenge challenge)
    {
        internal SiweChallenge Challenge { get; } = challenge;
        internal bool Consumed { get; set; }
    }
}
