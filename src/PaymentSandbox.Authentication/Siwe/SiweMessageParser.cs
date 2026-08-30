using System.Globalization;
using System.Numerics;
using Nethereum.Util;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Parses only the documented, canonical Week 15 subset of ERC-4361.</summary>
public static class SiweMessageParser
{
    public const int MaxMessageLength = 4 * 1024;
    private const string HeaderSuffix = " wants you to sign in with your Ethereum account:";

    public static SiweMessage Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxMessageLength ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal))
        {
            throw Malformed();
        }

        try
        {
            string[] lines = value.Split('\n');
            if (lines.Length != 11 || !lines[0].EndsWith(HeaderSuffix, StringComparison.Ordinal) ||
                lines[2].Length != 0 || lines[4].Length != 0)
            {
                throw Malformed();
            }

            string domain = lines[0][..^HeaderSuffix.Length];
            if (!TryParseCanonicalDomain(domain))
            {
                throw Malformed();
            }

            EvmAddress address = EvmAddress.Parse(lines[1]);
            string checksum = AddressUtil.Current.ConvertToChecksumAddress(address.Value);
            if (address.IsZero || !string.Equals(lines[1], checksum, StringComparison.Ordinal))
            {
                throw Malformed();
            }

            if (!SiweAuthenticationPolicy.IsAllowedStatement(lines[3]))
            {
                throw Malformed();
            }

            string uriText = RequirePrefix(lines[5], "URI: ");
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? requestUri) ||
                requestUri.Scheme != Uri.UriSchemeHttps ||
                requestUri.HostNameType != UriHostNameType.Dns ||
                !string.IsNullOrEmpty(requestUri.UserInfo) ||
                !string.IsNullOrEmpty(requestUri.Query) ||
                !string.IsNullOrEmpty(requestUri.Fragment) ||
                requestUri.AbsoluteUri.Length > 512 ||
                !string.Equals(uriText, requestUri.AbsoluteUri, StringComparison.Ordinal))
            {
                throw Malformed();
            }

            if (!string.Equals(lines[6], "Version: 1", StringComparison.Ordinal))
            {
                throw Malformed();
            }

            string chainText = RequirePrefix(lines[7], "Chain ID: ");
            if (!BigInteger.TryParse(chainText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out BigInteger chainId) || chainId <= BigInteger.Zero ||
                !string.Equals(chainText, chainId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                throw Malformed();
            }

            string nonce = RequirePrefix(lines[8], "Nonce: ");
            if (nonce.Length is < 8 or > 64 || !nonce.All(IsAsciiAlphaNumeric))
            {
                throw Malformed();
            }

            DateTimeOffset issuedAt = ParseTime(RequirePrefix(lines[9], "Issued At: "));
            DateTimeOffset expiresAt = ParseTime(
                RequirePrefix(lines[10], "Expiration Time: "));
            if (expiresAt <= issuedAt)
            {
                throw Malformed();
            }

            var parsed = new SiweMessage(
                domain, address, checksum, lines[3], requestUri,
                new EvmChainId(chainId), nonce, issuedAt, expiresAt);
            if (!string.Equals(parsed.Render(), value, StringComparison.Ordinal))
            {
                throw Malformed();
            }

            return parsed;
        }
        catch (SiweAuthenticationException)
        {
            throw;
        }
        catch (Exception)
        {
            // URI, number, address, and date parsers can expose attacker text in
            // their messages. Collapse all such failures to one bounded error.
            throw Malformed();
        }
    }

    private static string RequirePrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length
            ? value[prefix.Length..]
            : throw Malformed();

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            SiweTime.Rfc3339SecondsFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result)
            ? result
            : throw Malformed();

    private static bool TryParseCanonicalDomain(string value)
    {
        if (value.Length is 0 or > 255 || value.Contains('@', StringComparison.Ordinal) ||
            !Uri.TryCreate($"https://{value}", UriKind.Absolute, out Uri? origin) ||
            origin.HostNameType != UriHostNameType.Dns || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        return string.Equals(
            value, SiweAuthenticationPolicy.CanonicalAuthority(origin),
            StringComparison.Ordinal);
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static SiweAuthenticationException Malformed() => new(
        SiweAuthenticationErrorCode.MalformedMessage,
        "The SIWE message is not in the supported canonical format.");
}
