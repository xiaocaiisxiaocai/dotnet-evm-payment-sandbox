using System.Numerics;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Domain.Tests.Evm;

public sealed class EvmChainIdTests
{
    [Fact]
    public void Parse_NormalizesLeadingZeroes()
    {
        EvmChainId chainId = EvmChainId.Parse("00031337");

        Assert.Equal(new BigInteger(31_337), chainId.Value);
        Assert.Equal("31337", chainId.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    public void TryParse_RejectsNonPositiveOrAmbiguousDecimal(string? value)
    {
        Assert.False(EvmChainId.TryParse(value, out EvmChainId? chainId));
        Assert.Null(chainId);
    }

    [Fact]
    public void Constructor_AcceptsUint256MaximumAndRejectsOverflow()
    {
        BigInteger maximum = (BigInteger.One << 256) - BigInteger.One;

        Assert.Equal(maximum, new EvmChainId(maximum).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvmChainId(maximum + 1));
    }
}
