using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Authentication.Tests.Infrastructure;

namespace PaymentSandbox.Authentication.Tests.Siwe;

public sealed class SiweEoaSignatureVerifierTests
{
    [Theory]
    [InlineData("")]
    [InlineData("0x00")]
    [InlineData("not-hex")]
    public void Recover_RejectsMalformedSignaturesWithoutRetainingThem(string signature)
    {
        SiweAuthenticationException exception = Assert.Throws<SiweAuthenticationException>(
            () => SiweEoaSignatureVerifier.Recover("bounded message", signature));

        Assert.Equal(SiweAuthenticationErrorCode.InvalidSignature, exception.Code);
        if (signature.Length > 0)
        {
            Assert.DoesNotContain(signature, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Recover_RejectsLibrarySpecificZeroOneRecoveryIds()
    {
        var wallet = new TestEoa();
        const string message = "bounded message";
        byte[] signature = Convert.FromHexString(wallet.Sign(message).AsSpan(2));
        signature[64] = (byte)(signature[64] - 27);
        string changed = $"0x{Convert.ToHexStringLower(signature)}";

        SiweAuthenticationException exception = Assert.Throws<SiweAuthenticationException>(
            () => SiweEoaSignatureVerifier.Recover(message, changed));

        Assert.Equal(SiweAuthenticationErrorCode.InvalidSignature, exception.Code);
        Assert.DoesNotContain(changed, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recover_RejectsZeroSignatureScalars()
    {
        byte[] bytes = new byte[65];
        bytes[64] = 27;
        string signature = $"0x{Convert.ToHexStringLower(bytes)}";

        SiweAuthenticationException exception = Assert.Throws<SiweAuthenticationException>(
            () => SiweEoaSignatureVerifier.Recover("bounded message", signature));

        Assert.Equal(SiweAuthenticationErrorCode.InvalidSignature, exception.Code);
        Assert.DoesNotContain(signature, exception.ToString(), StringComparison.Ordinal);
    }
}
