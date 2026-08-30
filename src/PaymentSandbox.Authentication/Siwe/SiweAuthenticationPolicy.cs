using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Fixes the relying-party meaning and resource limits of Week 15 SIWE.</summary>
/// <remarks>
/// This policy deliberately supports a strict ERC-4361 subset: implicit HTTPS,
/// one DNS authority, one same-origin request URI, version 1, an EOA ERC-191
/// signature, no resources, and only local Anvil or Sepolia chain IDs.
/// </remarks>
public sealed record SiweAuthenticationPolicy
{
    public static readonly BigInteger LocalAnvilChainId = new(31_337);
    public static readonly BigInteger SepoliaChainId = new(11_155_111);

    public SiweAuthenticationPolicy(
        Uri origin,
        Uri requestUri,
        EvmChainId chainId,
        string statement,
        TimeSpan? challengeLifetime = null,
        TimeSpan? allowedClockSkew = null)
    {
        Origin = ValidateOrigin(origin);
        RequestUri = ValidateRequestUri(requestUri, Origin);
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        if (chainId.Value != LocalAnvilChainId && chainId.Value != SepoliaChainId)
        {
            throw new ArgumentException(
                "Week 15 SIWE allows only local Anvil (31337) or Sepolia (11155111).",
                nameof(chainId));
        }

        Statement = ValidateStatement(statement);
        ChallengeLifetime = challengeLifetime ?? TimeSpan.FromMinutes(5);
        if (ChallengeLifetime < TimeSpan.FromMinutes(1) ||
            ChallengeLifetime > TimeSpan.FromMinutes(10) ||
            ChallengeLifetime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(challengeLifetime));
        }

        AllowedClockSkew = allowedClockSkew ?? TimeSpan.FromSeconds(30);
        if (AllowedClockSkew < TimeSpan.Zero ||
            AllowedClockSkew > TimeSpan.FromMinutes(1) ||
            AllowedClockSkew.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedClockSkew));
        }

        Domain = CanonicalAuthority(Origin);
        Fingerprint = ComputeFingerprint(this);
    }

    public Uri Origin { get; }
    public string Domain { get; }
    public Uri RequestUri { get; }
    public EvmChainId ChainId { get; }
    public string Statement { get; }
    public TimeSpan ChallengeLifetime { get; }
    public TimeSpan AllowedClockSkew { get; }
    public string Fingerprint { get; }

    internal void ValidateMessage(SiweMessage message, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        nowUtc = nowUtc.ToUniversalTime();
        bool matches = string.Equals(message.Domain, Domain, StringComparison.Ordinal) &&
            message.RequestUri == RequestUri &&
            message.ChainId == ChainId &&
            string.Equals(message.Statement, Statement, StringComparison.Ordinal) &&
            message.ExpirationTimeUtc - message.IssuedAtUtc == ChallengeLifetime &&
            message.IssuedAtUtc <= nowUtc + AllowedClockSkew &&
            !message.Address.IsZero;
        if (!matches)
        {
            throw new SiweAuthenticationException(
                SiweAuthenticationErrorCode.PolicyMismatch,
                "The SIWE message does not match the active authentication policy.");
        }
    }

    internal static bool IsAllowedStatement(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        // ERC-4361 statement is reserved / unreserved / SP. This explicit set
        // rejects controls, line breaks, backslash, braces, and non-ASCII text.
        const string punctuation = "-._~:/?#[]@!$&'()*+,;= ";
        return value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ||
            punctuation.Contains(character, StringComparison.Ordinal));
    }

    internal static string CanonicalAuthority(Uri origin)
    {
        string host = origin.IdnHost.ToLowerInvariant();
        return origin.IsDefaultPort ? host : $"{host}:{origin.Port}";
    }

    private static Uri ValidateOrigin(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            value.HostNameType != UriHostNameType.Dns ||
            string.IsNullOrWhiteSpace(value.IdnHost) ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            value.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) ||
            value.AbsoluteUri.Length > 300)
        {
            throw new ArgumentException(
                "The SIWE origin must be a bounded HTTPS DNS origin without path, credentials, query, or fragment.",
                nameof(value));
        }

        return new Uri($"https://{CanonicalAuthority(value)}", UriKind.Absolute);
    }

    private static Uri ValidateRequestUri(Uri value, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            value.HostNameType != UriHostNameType.Dns ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) ||
            value.AbsoluteUri.Length > 512 ||
            !string.Equals(CanonicalAuthority(value), CanonicalAuthority(origin),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The SIWE request URI must be a bounded same-origin HTTPS URI without credentials, query, or fragment.",
                nameof(value));
        }

        return new Uri(value.AbsoluteUri, UriKind.Absolute);
    }

    private static string ValidateStatement(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsAllowedStatement(value)
            ? value
            : throw new ArgumentException(
                "The SIWE statement must contain 1-160 permitted printable ASCII characters.",
                nameof(value));
    }

    private static string ComputeFingerprint(SiweAuthenticationPolicy value)
    {
        string input = string.Join('\n',
            "payment-sandbox/siwe-authentication-policy/v1",
            value.Origin.AbsoluteUri,
            value.RequestUri.AbsoluteUri,
            value.ChainId.Value.ToString(CultureInfo.InvariantCulture),
            value.Statement,
            ((long)value.ChallengeLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ((long)value.AllowedClockSkew.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
