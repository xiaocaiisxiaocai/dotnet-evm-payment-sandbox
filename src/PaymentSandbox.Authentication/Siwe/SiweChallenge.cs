using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Server-issued, short-lived facts from which a wallet message is built.</summary>
public sealed record SiweChallenge
{
    internal SiweChallenge(
        string domain,
        Uri requestUri,
        EvmChainId chainId,
        string statement,
        string nonce,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expirationTimeUtc,
        string policyFingerprint)
    {
        Domain = domain;
        RequestUri = requestUri;
        ChainId = chainId;
        Statement = statement;
        Nonce = nonce;
        IssuedAtUtc = issuedAtUtc;
        ExpirationTimeUtc = expirationTimeUtc;
        PolicyFingerprint = policyFingerprint;
    }

    public string Domain { get; }
    public Uri RequestUri { get; }
    public EvmChainId ChainId { get; }
    public string Statement { get; }
    public string Nonce { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset ExpirationTimeUtc { get; }
    public string PolicyFingerprint { get; }

    /// <summary>Builds the canonical plaintext that the named wallet must display and sign.</summary>
    public string CreateMessage(EvmAddress address) => SiweMessage.Create(this, address).Render();

    public override string ToString() =>
        $"SIWE challenge for {Domain}, expires {SiweTime.Format(ExpirationTimeUtc)} (nonce redacted)";

    internal bool Matches(SiweMessage message) =>
        string.Equals(Domain, message.Domain, StringComparison.Ordinal) &&
        RequestUri == message.RequestUri &&
        ChainId == message.ChainId &&
        string.Equals(Statement, message.Statement, StringComparison.Ordinal) &&
        string.Equals(Nonce, message.Nonce, StringComparison.Ordinal) &&
        IssuedAtUtc == message.IssuedAtUtc &&
        ExpirationTimeUtc == message.ExpirationTimeUtc;
}
