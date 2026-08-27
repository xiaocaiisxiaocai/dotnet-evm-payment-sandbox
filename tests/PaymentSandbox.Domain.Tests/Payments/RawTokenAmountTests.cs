using System.Numerics;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Domain.Tests.Payments;

public sealed class RawTokenAmountTests
{
    [Fact]
    public void Constructor_AcceptsZeroAndUint256Maximum()
    {
        RawTokenAmount zero = new(BigInteger.Zero);
        RawTokenAmount maximum = new(RawTokenAmount.MaxValue);

        Assert.Equal(BigInteger.Zero, zero.Value);
        Assert.Equal(RawTokenAmount.MaxValue, maximum.Value);
    }

    [Fact]
    public void Constructor_PreservesValuesLargerThanUlong()
    {
        BigInteger expected = (BigInteger.One << 200) + 123;

        RawTokenAmount amount = new(expected);

        Assert.Equal(expected, amount.Value);
    }

    [Fact]
    public void Constructor_RejectsNegativeValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RawTokenAmount(BigInteger.MinusOne));
    }

    [Fact]
    public void Constructor_RejectsValueAboveUint256()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RawTokenAmount(RawTokenAmount.MaxValue + BigInteger.One));
    }

    [Fact]
    public void ToString_ReturnsInvariantRawIntegerWithoutRounding()
    {
        RawTokenAmount amount = new(BigInteger.Parse("1000000000000000001"));

        Assert.Equal("1000000000000000001", amount.ToString());
    }
}
