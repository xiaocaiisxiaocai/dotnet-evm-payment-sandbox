using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.Tests.PaymentIntents;

public sealed class PaymentIntentTests
{
    private static readonly EvmAddress Token =
        EvmAddress.Parse("0x2222222222222222222222222222222222222222");
    private static readonly EvmAddress Merchant =
        EvmAddress.Parse("0x3333333333333333333333333333333333333333");

    [Fact]
    public void Create_RecordsImmutableTermsWithoutClaimingChainProgress()
    {
        var terms = new PaymentIntentTerms(
            new EvmChainId(31_337),
            Token,
            Merchant,
            new RawTokenAmount(1_250_000));
        var localTime = new DateTimeOffset(2026, 8, 29, 18, 0, 0, TimeSpan.FromHours(8));

        PaymentIntent intent = PaymentIntent.Create(PaymentId.New(), terms, localTime);

        Assert.Equal(PaymentIntentStatus.Created, intent.Status);
        Assert.Same(terms, intent.Terms);
        Assert.Equal(TimeSpan.Zero, intent.CreatedAtUtc.Offset);
        Assert.Equal(localTime.ToUniversalTime(), intent.CreatedAtUtc);
    }

    [Fact]
    public void Terms_RejectZeroTokenMerchantAndAmount()
    {
        EvmAddress zero = EvmAddress.Parse($"0x{new string('0', 40)}");
        var chainId = new EvmChainId(31_337);

        Assert.Throws<ArgumentException>(
            () => new PaymentIntentTerms(chainId, zero, Merchant, new RawTokenAmount(1)));
        Assert.Throws<ArgumentException>(
            () => new PaymentIntentTerms(chainId, Token, zero, new RawTokenAmount(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PaymentIntentTerms(chainId, Token, Merchant, new RawTokenAmount(0)));
    }
}
