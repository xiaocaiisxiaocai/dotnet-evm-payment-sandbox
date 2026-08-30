using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Permits.Erc2612;

/// <summary>Canonical typed data ready for an external wallet to review and sign.</summary>
public sealed class Erc2612PermitDraft
{
    internal Erc2612PermitDraft(
        string policyFingerprint,
        EvmChainId chainId,
        EvmAddress token,
        string tokenName,
        string tokenVersion,
        EvmAddress owner,
        EvmAddress spender,
        RawTokenAmount value,
        BigInteger nonce,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset deadlineUtc,
        string typedDataJson,
        byte[] domainSeparator,
        byte[] structHash,
        byte[] digest)
    {
        PolicyFingerprint = policyFingerprint;
        ChainId = chainId;
        Token = token;
        TokenName = tokenName;
        TokenVersion = tokenVersion;
        Owner = owner;
        Spender = spender;
        Value = value;
        Nonce = nonce;
        IssuedAtUtc = issuedAtUtc;
        DeadlineUtc = deadlineUtc;
        TypedDataJson = typedDataJson;
        DomainSeparator = Hex(domainSeparator);
        StructHash = Hex(structHash);
        Digest = Hex(digest);
    }

    public string PolicyFingerprint { get; }
    public EvmChainId ChainId { get; }
    public EvmAddress Token { get; }
    public string TokenName { get; }
    public string TokenVersion { get; }
    public EvmAddress Owner { get; }
    public EvmAddress Spender { get; }
    public RawTokenAmount Value { get; }
    public BigInteger Nonce { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public BigInteger DeadlineUnixSeconds => new(DeadlineUtc.ToUnixTimeSeconds());
    public string TypedDataJson { get; }
    public string DomainSeparator { get; }
    public string StructHash { get; }
    public string Digest { get; }

    public override string ToString() =>
        $"ERC-2612 permit for {Owner.Value}, token {Token.Value}, value {Value}, " +
        $"nonce {Nonce}, deadline {DeadlineUtc:O} (typed data omitted)";

    private static string Hex(byte[] value) =>
        $"0x{Convert.ToHexStringLower(value)}";
}
