using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Permits.Erc2612;

/// <summary>Builds and verifies one strict ERC-2612 EIP-712 subset.</summary>
/// <remarks>
/// This service never signs, reads a private key, or fetches a token nonce. The
/// caller must supply a reviewed current nonce; Week 19 will add chain preflight.
/// </remarks>
public sealed class Erc2612PermitService
{
    private const string DomainType =
        "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)";
    private const string PermitType =
        "Permit(address owner,address spender,uint256 value,uint256 nonce,uint256 deadline)";
    private static readonly BigInteger Secp256k1HalfOrder = BigInteger.Parse(
        "7fffffffffffffffffffffffffffffff5d576e7357a4501ddfe92f46681b20a0",
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Erc2612PermitPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public Erc2612PermitService(Erc2612PermitPolicy policy, TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Creates wallet-readable typed data from one explicit token nonce.
    /// PaymentId and merchant are absent because ERC-2612 signs allowance only.
    /// </summary>
    public Erc2612PermitDraft CreateDraft(
        EvmAddress owner,
        RawTokenAmount value,
        BigInteger observedNonce)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsZero)
        {
            throw new ArgumentException("The permit owner cannot be zero.", nameof(owner));
        }

        if (value.Value.IsZero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "A permit value must be greater than zero.");
        }

        ValidateUint256(observedNonce, nameof(observedNonce));
        DateTimeOffset issuedAt = TruncateToSecond(
            _timeProvider.GetUtcNow().ToUniversalTime());
        DateTimeOffset deadline = issuedAt + _policy.PermitLifetime;
        BigInteger deadlineSeconds = new(deadline.ToUnixTimeSeconds());
        ValidateUint256(deadlineSeconds, nameof(deadline));

        byte[] domainSeparator = HashAbiWords(
            KeccakUtf8(DomainType),
            KeccakUtf8(_policy.TokenName),
            KeccakUtf8(_policy.TokenVersion),
            EncodeUint256(_policy.ChainId.Value),
            EncodeAddress(_policy.Token));
        byte[] structHash = HashAbiWords(
            KeccakUtf8(PermitType),
            EncodeAddress(owner),
            EncodeAddress(_policy.Spender),
            EncodeUint256(value.Value),
            EncodeUint256(observedNonce),
            EncodeUint256(deadlineSeconds));
        byte[] digest = Keccak(Concat([0x19, 0x01], domainSeparator, structHash));
        string typedDataJson = CreateTypedDataJson(
            owner,
            value,
            observedNonce,
            deadlineSeconds);

        return new Erc2612PermitDraft(
            _policy.Fingerprint,
            _policy.ChainId,
            _policy.Token,
            _policy.TokenName,
            _policy.TokenVersion,
            owner,
            _policy.Spender,
            value,
            observedNonce,
            issuedAt,
            deadline,
            typedDataJson,
            domainSeparator,
            structHash,
            digest);
    }

    /// <summary>Recovers the EIP-712 signer and returns bounded calldata parts.</summary>
    public VerifiedErc2612Permit Verify(
        Erc2612PermitDraft draft,
        string signature)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsurePolicy(draft);
        EnsureNotExpired(draft);
        if (!TryParseCanonicalSignature(signature, out byte v, out byte[] r, out byte[] s))
        {
            throw Error(
                Erc2612PermitErrorCode.InvalidSignature,
                "The ERC-2612 EOA signature is invalid.");
        }

        try
        {
            string recovered = Eip712TypedDataSigner.Current.RecoverFromSignatureV4(
                draft.TypedDataJson,
                signature);
            if (EvmAddress.Parse(recovered) != draft.Owner)
            {
                throw Error(
                    Erc2612PermitErrorCode.InvalidSignature,
                    "The ERC-2612 signature did not recover to its named owner.");
            }

            return new VerifiedErc2612Permit(draft, v, r, s);
        }
        catch (Erc2612PermitException)
        {
            throw;
        }
        catch (Exception)
        {
            // Library diagnostics may contain attacker-controlled signature or
            // typed-data fragments. Preserve neither at this boundary.
            throw Error(
                Erc2612PermitErrorCode.InvalidSignature,
                "The ERC-2612 EOA signature is invalid.");
        }
    }

    /// <summary>
    /// Rechecks the reviewed Router identity and creates unsigned calldata.
    /// The returned RequiredSender is essential because the Router uses
    /// msg.sender as permit owner and deliberately does not support relayers.
    /// </summary>
    public PreparedErc2612Payment PreparePayment(
        VerifiedPaymentRouterClient router,
        PaymentId paymentId,
        EvmAddress merchant,
        VerifiedErc2612Permit permit)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(paymentId);
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(permit);
        if (merchant.IsZero)
        {
            throw new ArgumentException("The merchant cannot be zero.", nameof(merchant));
        }

        EnsurePolicy(permit.Draft);
        EnsureNotExpired(permit.Draft);
        bool routerMatches = router.Identity.ChainId == _policy.ChainId.Value &&
            EvmAddress.TryParse(router.Identity.ContractAddress, out EvmAddress? address) &&
            address == _policy.Spender;
        if (!routerMatches)
        {
            throw Error(
                Erc2612PermitErrorCode.RouterMismatch,
                "The verified Router does not match this permit's chain and spender.");
        }

        EncodedPaymentRouterCall call = router.EncodePayWithPermit(
            paymentId,
            permit.Draft.Token.Value,
            merchant.Value,
            permit.Draft.Value,
            permit.Draft.DeadlineUnixSeconds,
            permit.V,
            permit.R,
            permit.S);
        return new PreparedErc2612Payment(
            paymentId,
            permit.Draft.Owner,
            permit.Draft.Token,
            merchant,
            permit.Draft.Value,
            permit.Draft.Digest,
            call);
    }

    private string CreateTypedDataJson(
        EvmAddress owner,
        RawTokenAmount value,
        BigInteger nonce,
        BigInteger deadline)
    {
        var typedData = new TypedDataDocument(
            new Dictionary<string, TypedDataMember[]>(StringComparer.Ordinal)
            {
                ["EIP712Domain"] =
                [
                    new("name", "string"),
                    new("version", "string"),
                    new("chainId", "uint256"),
                    new("verifyingContract", "address"),
                ],
                ["Permit"] =
                [
                    new("owner", "address"),
                    new("spender", "address"),
                    new("value", "uint256"),
                    new("nonce", "uint256"),
                    new("deadline", "uint256"),
                ],
            },
            "Permit",
            new TypedDataDomain(
                _policy.TokenName,
                _policy.TokenVersion,
                Decimal(_policy.ChainId.Value),
                _policy.Token.Value),
            new TypedDataPermit(
                owner.Value,
                _policy.Spender.Value,
                Decimal(value.Value),
                Decimal(nonce),
                Decimal(deadline)));
        return JsonSerializer.Serialize(typedData, JsonOptions);
    }

    private void EnsurePolicy(Erc2612PermitDraft draft)
    {
        if (!string.Equals(
                draft.PolicyFingerprint,
                _policy.Fingerprint,
                StringComparison.Ordinal))
        {
            throw Error(
                Erc2612PermitErrorCode.PolicyMismatch,
                "The permit draft does not match the active reviewed policy.");
        }
    }

    private void EnsureNotExpired(Erc2612PermitDraft draft)
    {
        if (_timeProvider.GetUtcNow().ToUniversalTime() >= draft.DeadlineUtc)
        {
            throw Error(
                Erc2612PermitErrorCode.PermitExpired,
                "The permit deadline has expired.");
        }
    }

    private static bool TryParseCanonicalSignature(
        string? signature,
        out byte v,
        out byte[] r,
        out byte[] s)
    {
        v = 0;
        r = [];
        s = [];
        if (signature is null || signature.Length != 132 || signature[0] != '0' ||
            (signature[1] != 'x' && signature[1] != 'X'))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(signature.AsSpan(2));
        }
        catch (FormatException)
        {
            return false;
        }

        v = bytes[64];
        r = bytes[..32];
        s = bytes[32..64];
        BigInteger rValue = new(r, isUnsigned: true, isBigEndian: true);
        BigInteger sValue = new(s, isUnsigned: true, isBigEndian: true);
        return v is 27 or 28 && !rValue.IsZero &&
            !sValue.IsZero && sValue <= Secp256k1HalfOrder;
    }

    private static byte[] HashAbiWords(params byte[][] words)
    {
        if (words.Any(word => word.Length != 32))
        {
            throw new InvalidOperationException("Every EIP-712 ABI word must be 32 bytes.");
        }

        return Keccak(Concat(words));
    }

    private static byte[] EncodeAddress(EvmAddress address)
    {
        var encoded = new byte[32];
        address.ToBytes().CopyTo(encoded, 12);
        return encoded;
    }

    private static byte[] EncodeUint256(BigInteger value)
    {
        ValidateUint256(value, nameof(value));
        byte[] source = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var encoded = new byte[32];
        source.CopyTo(encoded, encoded.Length - source.Length);
        return encoded;
    }

    private static void ValidateUint256(BigInteger value, string parameterName)
    {
        if (value < BigInteger.Zero || value > RawTokenAmount.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, value, "The value must fit in an EVM uint256.");
        }
    }

    private static byte[] KeccakUtf8(string value) =>
        Keccak(Encoding.UTF8.GetBytes(value));

    private static byte[] Keccak(byte[] value) =>
        Sha3Keccack.Current.CalculateHash(value);

    private static byte[] Concat(params byte[][] values)
    {
        int length = values.Sum(value => value.Length);
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static string Decimal(BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static Erc2612PermitException Error(
        Erc2612PermitErrorCode code,
        string message) => new(code, message);

    private sealed record TypedDataMember(string Name, string Type);

    private sealed record TypedDataDomain(
        string Name,
        string Version,
        string ChainId,
        string VerifyingContract);

    private sealed record TypedDataPermit(
        string Owner,
        string Spender,
        string Value,
        string Nonce,
        string Deadline);

    private sealed record TypedDataDocument(
        Dictionary<string, TypedDataMember[]> Types,
        string PrimaryType,
        TypedDataDomain Domain,
        TypedDataPermit Message);
}
