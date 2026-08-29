using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Domain.Tests.Evm;

public sealed class EvmAddressTests
{
    [Fact]
    public void Parse_NormalizesPrefixAndHexCasing()
    {
        var address = EvmAddress.Parse("0XABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD");

        Assert.Equal("0xabcdefabcdefabcdefabcdefabcdefabcdefabcd", address.Value);
        Assert.False(address.IsZero);
        Assert.Equal(20, address.ToBytes().Length);
    }

    [Fact]
    public void Parse_PreservesZeroAsSyntacticallyValidAddress()
    {
        var address = EvmAddress.Parse($"0x{new string('0', 40)}");

        Assert.True(address.IsZero);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x01")]
    [InlineData("abcdefabcdefabcdefabcdefabcdefabcdefabcd")]
    [InlineData("0xgggggggggggggggggggggggggggggggggggggggg")]
    public void TryParse_RejectsMalformedAddress(string? value)
    {
        Assert.False(EvmAddress.TryParse(value, out EvmAddress? address));
        Assert.Null(address);
    }
}
