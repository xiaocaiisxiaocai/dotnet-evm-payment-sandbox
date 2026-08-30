using System.Numerics;
using Nethereum.Signer;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Recovers an EOA from the ERC-191 personal-sign bytes required by SIWE.</summary>
public static class SiweEoaSignatureVerifier
{
    public static EvmAddress Recover(string message, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!TryParseSignature(signature, out byte[] bytes))
        {
            throw Invalid();
        }

        try
        {
            // Standard wallet signatures are r[32] || s[32] || v[1]. Requiring
            // v=27/28 rejects ambiguous library-specific 0/1 encodings.
            if (bytes[64] is not (27 or 28) ||
                new BigInteger(bytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true).IsZero ||
                new BigInteger(bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true).IsZero)
            {
                throw Invalid();
            }

            string recovered = new EthereumMessageSigner()
                .EncodeUTF8AndEcRecover(message, signature);
            return EvmAddress.Parse(recovered);
        }
        catch (SiweAuthenticationException)
        {
            throw;
        }
        catch (Exception)
        {
            // Recovery libraries can include attacker-controlled message or
            // signature fragments in diagnostics. Keep neither one.
            throw Invalid();
        }
    }

    private static bool TryParseSignature(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 132 || value[0] != '0' ||
            (value[1] != 'x' && value[1] != 'X'))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value.AsSpan(2));
            return bytes.Length == 65;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static SiweAuthenticationException Invalid() => new(
        SiweAuthenticationErrorCode.InvalidSignature,
        "The ERC-191 EOA signature is invalid for this SIWE message.");
}
