using System.Numerics;
using System.Reflection;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts.MessageEncodingServices;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Contracts.PaymentRouter.ContractDefinition;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Contracts.Tests.PaymentRouter;

public sealed class VerifiedPaymentRouterClientTests
{
    private const string RouterAddress = "0x1111111111111111111111111111111111111111";
    private const string TokenAddress = "0x2222222222222222222222222222222222222222";
    private const string MerchantAddress = "0x3333333333333333333333333333333333333333";
    private const string ZeroByteCodeHash =
        "0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a";

    [Fact]
    public async Task EncodePay_UsesReviewedSelectorAndRoundTripsTypedArguments()
    {
        VerifiedPaymentRouterClient client = await ConnectAsync();
        PaymentId paymentId = PaymentId.Parse($"0x{new string('a', 64)}");

        EncodedPaymentRouterCall call = client.EncodePay(
            paymentId,
            TokenAddress,
            MerchantAddress,
            new RawTokenAmount(123_456));

        Assert.Equal(RouterAddress, call.ContractAddress);
        Assert.StartsWith("0x76bbf425", call.Data);
        Assert.Equal(2 + (132 * 2), call.Data.Length);

        var decoder = new FunctionMessageEncodingService<PayFunction>();
        PayFunction decoded = decoder.DecodeInput(new PayFunction(), call.Data);
        Assert.Equal(paymentId.ToBytes(), decoded.PaymentId);
        Assert.Equal(TokenAddress, decoded.Token);
        Assert.Equal(MerchantAddress, decoded.Merchant);
        Assert.Equal(new BigInteger(123_456), decoded.Amount);
    }

    [Fact]
    public async Task EncodePayWithPermit_UsesReviewedSelectorAndCopiesSignatureParts()
    {
        VerifiedPaymentRouterClient client = await ConnectAsync();
        PaymentId paymentId = PaymentId.Parse($"0x{new string('b', 64)}");
        byte[] r = Enumerable.Repeat((byte)0x44, 32).ToArray();
        byte[] s = Enumerable.Repeat((byte)0x55, 32).ToArray();

        EncodedPaymentRouterCall call = client.EncodePayWithPermit(
            paymentId,
            TokenAddress,
            MerchantAddress,
            new RawTokenAmount(99),
            permitDeadline: 1_900_000_000,
            v: 28,
            r,
            s);

        Assert.StartsWith("0x1f2b568e", call.Data);
        Assert.Equal(2 + (260 * 2), call.Data.Length);

        var decoder = new FunctionMessageEncodingService<PayWithPermitFunction>();
        PayWithPermitFunction decoded = decoder.DecodeInput(new PayWithPermitFunction(), call.Data);
        Assert.Equal(new BigInteger(1_900_000_000), decoded.PermitDeadline);
        Assert.Equal((byte)28, decoded.V);
        Assert.Equal(r, decoded.R);
        Assert.Equal(s, decoded.S);
    }

    [Fact]
    public async Task Encoder_RejectsInputsThatRouterWouldImmediatelyReject()
    {
        VerifiedPaymentRouterClient client = await ConnectAsync();
        PaymentId paymentId = PaymentId.New();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => client.EncodePay(paymentId, TokenAddress, MerchantAddress, new RawTokenAmount(0)));
        Assert.Throws<ArgumentException>(
            () => client.EncodePay(paymentId, "0x0", MerchantAddress, new RawTokenAmount(1)));
        Assert.Throws<ArgumentException>(
            () => client.EncodePay(paymentId, TokenAddress, RouterAddress, new RawTokenAmount(1)));
    }

    [Fact]
    public async Task EncodePayWithPermit_RejectsInvalidUint256AndSignatureWidths()
    {
        VerifiedPaymentRouterClient client = await ConnectAsync();
        PaymentId paymentId = PaymentId.New();
        byte[] validPart = new byte[32];

        Assert.Throws<ArgumentOutOfRangeException>(() => client.EncodePayWithPermit(
            paymentId,
            TokenAddress,
            MerchantAddress,
            new RawTokenAmount(1),
            -1,
            27,
            validPart,
            validPart));
        Assert.Throws<ArgumentException>(() => client.EncodePayWithPermit(
            paymentId,
            TokenAddress,
            MerchantAddress,
            new RawTokenAmount(1),
            1,
            27,
            new byte[31],
            validPart));
    }

    [Fact]
    public void ContractDefinition_PreservesReviewedEventIndexing()
    {
        EventAttribute eventAttribute = Assert.IsType<EventAttribute>(
            typeof(PaymentRecordedEventDto).GetCustomAttribute<EventAttribute>());
        Assert.Equal("PaymentRecorded", eventAttribute.Name);

        PropertyInfo[] properties = typeof(PaymentRecordedEventDto).GetProperties();
        Dictionary<string, bool> indexedByName = properties.ToDictionary(
            property => property.Name,
            property => property.GetCustomAttribute<ParameterAttribute>()!.Parameter.Indexed);

        Assert.True(indexedByName[nameof(PaymentRecordedEventDto.PaymentId)]);
        Assert.True(indexedByName[nameof(PaymentRecordedEventDto.Payer)]);
        Assert.False(indexedByName[nameof(PaymentRecordedEventDto.Token)]);
        Assert.True(indexedByName[nameof(PaymentRecordedEventDto.Merchant)]);
        Assert.False(indexedByName[nameof(PaymentRecordedEventDto.Amount)]);
    }

    [Fact]
    public void ReviewedRuntimeHash_MatchesCommittedWeekFourBaseline()
    {
        Assert.Equal(
            "0x8308fbd23f6bd4bcb4284281ab9388b2a437297aa512a8308b4c2e390205e92c",
            PaymentRouterArtifact.RuntimeCodeKeccak256);
    }

    private static async Task<VerifiedPaymentRouterClient> ConnectAsync()
    {
        var rpc = new FixedIdentityRpc();
        var connector = new PaymentRouterConnector(rpc);
        var policy = new PaymentRouterTrustPolicy(
            31_337,
            RouterAddress,
            ZeroByteCodeHash);

        return await connector.ConnectAsync(policy, TestContext.Current.CancellationToken);
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
