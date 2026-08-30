using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Permits.Erc2612;

/// <summary>Reviewed EIP-712 domain, spender, and lifetime for one permit token.</summary>
public sealed record Erc2612PermitPolicy
{
    public static readonly BigInteger LocalAnvilChainId = new(31_337);
    public static readonly BigInteger SepoliaChainId = new(11_155_111);

    public Erc2612PermitPolicy(
        EvmChainId chainId,
        EvmAddress token,
        string tokenName,
        string tokenVersion,
        EvmAddress spender,
        TimeSpan permitLifetime)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        if (chainId.Value != LocalAnvilChainId && chainId.Value != SepoliaChainId)
        {
            throw new ArgumentException(
                "Permit construction allows only local Anvil or Sepolia.",
                nameof(chainId));
        }

        Token = RequireNonZero(token, nameof(token));
        Spender = RequireNonZero(spender, nameof(spender));
        if (Token == Spender)
        {
            throw new ArgumentException(
                "The permit token and spender contracts must be different.",
                nameof(spender));
        }

        TokenName = RequirePrintableAscii(tokenName, 64, nameof(tokenName));
        TokenVersion = RequirePrintableAscii(tokenVersion, 16, nameof(tokenVersion));
        if (permitLifetime < TimeSpan.FromMinutes(1) ||
            permitLifetime > TimeSpan.FromHours(1) ||
            permitLifetime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitLifetime),
                permitLifetime,
                "Permit lifetime must be from one minute through one hour in whole seconds.");
        }

        PermitLifetime = permitLifetime;
        Fingerprint = ComputeFingerprint(this);
    }

    public EvmChainId ChainId { get; }
    public EvmAddress Token { get; }
    public string TokenName { get; }
    public string TokenVersion { get; }
    public EvmAddress Spender { get; }
    public TimeSpan PermitLifetime { get; }
    public string Fingerprint { get; }

    private static EvmAddress RequireNonZero(EvmAddress value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return !value.IsZero
            ? value
            : throw new ArgumentException("The address cannot be zero.", parameterName);
    }

    private static string RequirePrintableAscii(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(character => character is < ' ' or > '~'))
        {
            throw new ArgumentException(
                $"The value must contain 1-{maximumLength} printable ASCII characters.",
                parameterName);
        }

        return value;
    }

    private static string ComputeFingerprint(Erc2612PermitPolicy policy)
    {
        string input = string.Join(
            '\n',
            "payment-sandbox/erc2612-permit-policy/v1",
            policy.ChainId.Value.ToString(CultureInfo.InvariantCulture),
            policy.Token.Value,
            policy.TokenName,
            policy.TokenVersion,
            policy.Spender.Value,
            ((long)policy.PermitLifetime.TotalSeconds).ToString(
                CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
