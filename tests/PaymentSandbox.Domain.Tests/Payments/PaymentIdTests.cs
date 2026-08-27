using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.Tests.Payments;

public sealed class PaymentIdTests
{
    [Fact]
    public void New_CreatesCanonicalNonZeroId()
    {
        PaymentId paymentId = PaymentId.New();

        Assert.StartsWith("0x", paymentId.Value);
        Assert.Equal(66, paymentId.Value.Length);
        Assert.NotEqual($"0x{new string('0', 64)}", paymentId.Value);
        Assert.Equal(paymentId.Value, paymentId.Value.ToLowerInvariant());
    }

    [Fact]
    public void Parse_NormalizesUppercaseHexAndRoundTripsBytes()
    {
        byte[] bytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        string uppercase = $"0X{Convert.ToHexString(bytes)}";

        PaymentId paymentId = PaymentId.Parse(uppercase);

        Assert.Equal($"0x{Convert.ToHexString(bytes).ToLowerInvariant()}", paymentId.Value);
        Assert.Equal(bytes, paymentId.ToBytes());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("0x01")]
    [InlineData("0xgggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")]
    public void TryParse_RejectsMalformedOrReservedValues(string? value)
    {
        bool parsed = PaymentId.TryParse(value, out PaymentId? paymentId);

        Assert.False(parsed);
        Assert.Null(paymentId);
    }

    [Fact]
    public void FromBytes_RejectsWrongLengthAndAllZeroValue()
    {
        Assert.Throws<ArgumentException>(() => PaymentId.FromBytes(new byte[31]));
        Assert.Throws<ArgumentException>(() => PaymentId.FromBytes(new byte[32]));
    }

    [Fact]
    public void EqualCanonicalIds_HaveValueEquality()
    {
        byte[] bytes = Enumerable.Repeat((byte)0xab, 32).ToArray();

        PaymentId first = PaymentId.FromBytes(bytes);
        PaymentId second = PaymentId.Parse(first.Value.ToUpperInvariant());

        Assert.Equal(first, second);
    }
}
