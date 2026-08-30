using System.Globalization;
using Nethereum.Util;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Parsed facts from the strict Week 15 ERC-4361 message subset.</summary>
public sealed record SiweMessage
{
    internal SiweMessage(
        string domain,
        EvmAddress address,
        string checksumAddress,
        string statement,
        Uri requestUri,
        EvmChainId chainId,
        string nonce,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expirationTimeUtc)
    {
        Domain = domain;
        Address = address;
        ChecksumAddress = checksumAddress;
        Statement = statement;
        RequestUri = requestUri;
        ChainId = chainId;
        Nonce = nonce;
        IssuedAtUtc = issuedAtUtc;
        ExpirationTimeUtc = expirationTimeUtc;
    }

    public string Domain { get; }
    public EvmAddress Address { get; }
    public string ChecksumAddress { get; }
    public string Statement { get; }
    public Uri RequestUri { get; }
    public EvmChainId ChainId { get; }
    public string Nonce { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset ExpirationTimeUtc { get; }

    /// <summary>Returns the exact human-readable bytes that ERC-191 signs.</summary>
    public string Render() => string.Join('\n',
        $"{Domain} wants you to sign in with your Ethereum account:",
        ChecksumAddress,
        string.Empty,
        Statement,
        string.Empty,
        $"URI: {RequestUri.AbsoluteUri}",
        "Version: 1",
        $"Chain ID: {ChainId.Value.ToString(CultureInfo.InvariantCulture)}",
        $"Nonce: {Nonce}",
        $"Issued At: {SiweTime.Format(IssuedAtUtc)}",
        $"Expiration Time: {SiweTime.Format(ExpirationTimeUtc)}");

    public override string ToString() =>
        $"SIWE message for {Address.Value} at {Domain} (message and nonce redacted)";

    internal static SiweMessage Create(SiweChallenge challenge, EvmAddress address)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsZero)
        {
            throw new ArgumentException("A SIWE signer address cannot be zero.", nameof(address));
        }

        string checksum = AddressUtil.Current.ConvertToChecksumAddress(address.Value);
        return new SiweMessage(
            challenge.Domain,
            address,
            checksum,
            challenge.Statement,
            challenge.RequestUri,
            challenge.ChainId,
            challenge.Nonce,
            challenge.IssuedAtUtc,
            challenge.ExpirationTimeUtc);
    }
}

internal static class SiweTime
{
    internal const string Rfc3339SecondsFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
    }

    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Rfc3339SecondsFormat, CultureInfo.InvariantCulture);
}
