using System.Numerics;
using System.Text.Json;
using Nethereum.Contracts.MessageEncodingServices;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Contracts.PaymentRouter.ContractDefinition;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Erc2612;

namespace PaymentSandbox.Permits.Tests.Erc2612;

public sealed class Erc2612PermitServiceTests
{
    private const string RouterAddress = "0x1111111111111111111111111111111111111111";
    private const string TokenAddress = "0x2222222222222222222222222222222222222222";
    private const string MerchantAddress = "0x3333333333333333333333333333333333333333";
    private const string OtherRouterAddress = "0x4444444444444444444444444444444444444444";
    private const string ZeroByteCodeHash =
        "0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDraft_ProducesCanonicalEip712BytesAndWalletJson()
    {
        var clock = new MutableTimeProvider(Now.AddMilliseconds(987));
        Erc2612PermitService service = CreateService(clock);
        var wallet = new TestWallet();

        Erc2612PermitDraft draft = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(1_250_000),
            observedNonce: 7);
        byte[] libraryEncoding = Eip712TypedDataSigner.Current.EncodeTypedData(
            draft.TypedDataJson);
        byte[] expectedEncoding =
        [
            0x19,
            0x01,
            .. Convert.FromHexString(draft.DomainSeparator.AsSpan(2)),
            .. Convert.FromHexString(draft.StructHash.AsSpan(2)),
        ];
        string libraryDigest =
            $"0x{Convert.ToHexStringLower(Sha3Keccack.Current.CalculateHash(libraryEncoding))}";

        Assert.Equal(expectedEncoding, libraryEncoding);
        Assert.Equal(libraryDigest, draft.Digest);
        Assert.Equal(Now, draft.IssuedAtUtc);
        Assert.Equal(Now.AddMinutes(10), draft.DeadlineUtc);
        Assert.Equal("7", draft.Nonce.ToString());
        Assert.Equal(TokenAddress, draft.Token.Value);
        Assert.Equal(RouterAddress, draft.Spender.Value);
        Assert.Equal(66, draft.DomainSeparator.Length);
        Assert.Equal(66, draft.StructHash.Length);
        Assert.Equal(66, draft.Digest.Length);

        using JsonDocument json = JsonDocument.Parse(draft.TypedDataJson);
        JsonElement root = json.RootElement;
        Assert.Equal("Permit", root.GetProperty("primaryType").GetString());
        Assert.Equal("31337", root.GetProperty("domain").GetProperty("chainId").GetString());
        Assert.Equal(
            TokenAddress,
            root.GetProperty("domain").GetProperty("verifyingContract").GetString());
        Assert.Equal(
            "1250000",
            root.GetProperty("message").GetProperty("value").GetString());
        Assert.False(root.GetProperty("message").TryGetProperty("paymentId", out _));
        Assert.False(root.GetProperty("message").TryGetProperty("merchant", out _));
    }

    [Fact]
    public void Verify_RecoversOwnerAndReturnsDefensiveSignatureParts()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        Erc2612PermitDraft draft = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(99),
            observedNonce: 0);
        string signature = wallet.Sign(draft.TypedDataJson);

        VerifiedErc2612Permit verified = service.Verify(draft, signature);
        byte[] firstRead = verified.R;
        firstRead[0] ^= 0xff;

        Assert.True(verified.V is 27 or 28);
        Assert.Equal(32, verified.R.Length);
        Assert.Equal(32, verified.S.Length);
        Assert.NotEqual(firstRead, verified.R);
        Assert.DoesNotContain(signature, verified.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(draft.TypedDataJson, draft.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SignatureCannotCrossValueNonceOrDomain()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        Erc2612PermitDraft original = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(100),
            observedNonce: 5);
        string signature = wallet.Sign(original.TypedDataJson);
        Erc2612PermitDraft changedValue = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(101),
            observedNonce: 5);
        Erc2612PermitDraft changedNonce = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(100),
            observedNonce: 6);
        AssertInvalidSignature(() => service.Verify(changedValue, signature));
        AssertInvalidSignature(() => service.Verify(changedNonce, signature));
        Erc2612PermitService[] changedDomainServices =
        [
            CreateService(new MutableTimeProvider(Now), tokenName: "Different Token"),
            CreateService(new MutableTimeProvider(Now), chainId: 11_155_111),
            CreateService(
                new MutableTimeProvider(Now),
                tokenAddress: "0x6666666666666666666666666666666666666666"),
            CreateService(
                new MutableTimeProvider(Now),
                spenderAddress: "0x7777777777777777777777777777777777777777"),
        ];
        foreach (Erc2612PermitService changedDomainService in changedDomainServices)
        {
            Erc2612PermitDraft changedDomain = changedDomainService.CreateDraft(
                wallet.Address,
                new RawTokenAmount(100),
                observedNonce: 5);
            AssertInvalidSignature(() => changedDomainService.Verify(changedDomain, signature));
            Erc2612PermitException policyMismatch = Assert.Throws<Erc2612PermitException>(
                () => changedDomainService.Verify(original, signature));
            Assert.Equal(Erc2612PermitErrorCode.PolicyMismatch, policyMismatch.Code);
        }
    }

    [Fact]
    public void Verify_RejectsWrongSignerAndMalleableHighS()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));
        var expectedWallet = new TestWallet();
        var wrongWallet = new TestWallet();
        Erc2612PermitDraft draft = service.CreateDraft(
            expectedWallet.Address,
            new RawTokenAmount(1),
            observedNonce: 0);
        string valid = expectedWallet.Sign(draft.TypedDataJson);
        string highS = MakeHighSSignature(valid);

        AssertInvalidSignature(() => service.Verify(draft, wrongWallet.Sign(draft.TypedDataJson)));
        AssertInvalidSignature(() => service.Verify(draft, highS));
    }

    [Fact]
    public void ExactDeadline_IsExpiredBeforeSignatureRecoveryOrPreparation()
    {
        var clock = new MutableTimeProvider(Now);
        Erc2612PermitService service = CreateService(clock);
        var wallet = new TestWallet();
        Erc2612PermitDraft draft = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(1),
            observedNonce: 0);
        VerifiedErc2612Permit verified = service.Verify(
            draft,
            wallet.Sign(draft.TypedDataJson));
        clock.Advance(TimeSpan.FromMinutes(10));

        Erc2612PermitException verifyExpired = Assert.Throws<Erc2612PermitException>(
            () => service.Verify(draft, "not-a-signature"));
        Erc2612PermitException prepareExpired = Assert.Throws<Erc2612PermitException>(
            () => service.PreparePayment(
                CreateRouterAsync(RouterAddress).GetAwaiter().GetResult(),
                PaymentId.New(),
                EvmAddress.Parse(MerchantAddress),
                verified));

        Assert.Equal(Erc2612PermitErrorCode.PermitExpired, verifyExpired.Code);
        Assert.Equal(Erc2612PermitErrorCode.PermitExpired, prepareExpired.Code);
    }

    [Fact]
    public async Task PreparePayment_BindsVerifiedRouterAndRequiredSender()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        Erc2612PermitDraft draft = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(1_250_000),
            observedNonce: 9);
        VerifiedErc2612Permit verified = service.Verify(
            draft,
            wallet.Sign(draft.TypedDataJson));
        PaymentId paymentId = PaymentId.New();

        PreparedErc2612Payment prepared = service.PreparePayment(
            await CreateRouterAsync(RouterAddress),
            paymentId,
            EvmAddress.Parse(MerchantAddress),
            verified);
        var decoder = new FunctionMessageEncodingService<PayWithPermitFunction>();
        PayWithPermitFunction decoded = decoder.DecodeInput(
            new PayWithPermitFunction(),
            prepared.Call.Data);

        Assert.Equal(wallet.Address, prepared.RequiredSender);
        Assert.Equal(paymentId, prepared.PaymentId);
        Assert.Equal(TokenAddress, decoded.Token);
        Assert.Equal(MerchantAddress, decoded.Merchant);
        Assert.Equal(new BigInteger(1_250_000), decoded.Amount);
        Assert.Equal(new BigInteger(draft.DeadlineUtc.ToUnixTimeSeconds()),
            decoded.PermitDeadline);
        Assert.Equal(verified.V, decoded.V);
        Assert.Equal(verified.R, decoded.R);
        Assert.Equal(verified.S, decoded.S);
        Assert.StartsWith("0x1f2b568e", prepared.Call.Data);
        Assert.DoesNotContain(prepared.Call.Data, prepared.ToString(), StringComparison.Ordinal);
        Assert.Contains("calldata redacted", prepared.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparePayment_RejectsAnotherRouterIdentity()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));
        var wallet = new TestWallet();
        Erc2612PermitDraft draft = service.CreateDraft(
            wallet.Address,
            new RawTokenAmount(1),
            observedNonce: 0);
        VerifiedErc2612Permit verified = service.Verify(
            draft,
            wallet.Sign(draft.TypedDataJson));
        VerifiedPaymentRouterClient wrongRouter = await CreateRouterAsync(OtherRouterAddress);

        Erc2612PermitException mismatch = Assert.Throws<Erc2612PermitException>(
            () => service.PreparePayment(
                wrongRouter,
                PaymentId.New(),
                EvmAddress.Parse(MerchantAddress),
                verified));

        Assert.Equal(Erc2612PermitErrorCode.RouterMismatch, mismatch.Code);
    }

    [Fact]
    public void Draft_RejectsZeroOwnerValueAndOutOfRangeNonce()
    {
        Erc2612PermitService service = CreateService(new MutableTimeProvider(Now));

        Assert.Throws<ArgumentException>(() => service.CreateDraft(
            EvmAddress.Parse("0x0000000000000000000000000000000000000000"),
            new RawTokenAmount(1),
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.CreateDraft(
            EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
            new RawTokenAmount(0),
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.CreateDraft(
            EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
            new RawTokenAmount(1),
            -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.CreateDraft(
            EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
            new RawTokenAmount(1),
            RawTokenAmount.MaxValue + 1));
    }

    [Theory]
    [InlineData(59)]
    [InlineData(3601)]
    public void Policy_RejectsLifetimeOutsideReviewedBounds(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Erc2612PermitPolicy(
            new EvmChainId(31_337),
            EvmAddress.Parse(TokenAddress),
            "Test USDC",
            "1",
            EvmAddress.Parse(RouterAddress),
            TimeSpan.FromSeconds(seconds)));
    }

    private static Erc2612PermitService CreateService(
        TimeProvider clock,
        string tokenName = "Test USDC",
        long chainId = 31_337,
        string tokenAddress = TokenAddress,
        string spenderAddress = RouterAddress) =>
        new(
            new Erc2612PermitPolicy(
                new EvmChainId(chainId),
                EvmAddress.Parse(tokenAddress),
                tokenName,
                "1",
                EvmAddress.Parse(spenderAddress),
                TimeSpan.FromMinutes(10)),
            clock);

    private static async Task<VerifiedPaymentRouterClient> CreateRouterAsync(string address)
    {
        var connector = new PaymentRouterConnector(new FixedIdentityRpc());
        return await connector.ConnectAsync(
            new PaymentRouterTrustPolicy(31_337, address, ZeroByteCodeHash),
            TestContext.Current.CancellationToken);
    }

    private static void AssertInvalidSignature(Action action)
    {
        Erc2612PermitException exception = Assert.Throws<Erc2612PermitException>(action);
        Assert.Equal(Erc2612PermitErrorCode.InvalidSignature, exception.Code);
    }

    private static string MakeHighSSignature(string canonicalSignature)
    {
        byte[] bytes = Convert.FromHexString(canonicalSignature.AsSpan(2));
        BigInteger order = BigInteger.Parse(
            "0fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141",
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
        BigInteger lowS = new(bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        byte[] highS = (order - lowS).ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Clear(bytes, 32, 32);
        highS.CopyTo(bytes, 64 - highS.Length);
        bytes[64] = bytes[64] == 27 ? (byte)28 : (byte)27;
        return $"0x{Convert.ToHexStringLower(bytes)}";
    }

    private sealed class TestWallet
    {
        private readonly EthECKey _key = EthECKey.GenerateKey();

        internal EvmAddress Address => EvmAddress.Parse(_key.GetPublicAddress());

        internal string Sign(string typedDataJson) =>
            Eip712TypedDataSigner.Current.SignTypedDataV4(typedDataJson, _key);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class FixedIdentityRpc : IPaymentRouterIdentityRpc
    {
        public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BigInteger(31_337));

        public Task<string> GetCodeAsync(
            string contractAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("0x00");
    }
}
