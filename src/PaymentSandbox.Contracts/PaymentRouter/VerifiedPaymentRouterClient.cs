using System.Numerics;
using Nethereum.Contracts.MessageEncodingServices;
using Nethereum.Util;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter.ContractDefinition;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Contracts.PaymentRouter;

/// <summary>Local typed encoder gated by a successful Router identity check.</summary>
public sealed class VerifiedPaymentRouterClient
{
    internal VerifiedPaymentRouterClient(VerifiedPaymentRouterIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public VerifiedPaymentRouterIdentity Identity { get; }

    /// <summary>Encodes pay(bytes32,address,address,uint256) without signing or sending.</summary>
    public EncodedPaymentRouterCall EncodePay(
        PaymentId paymentId,
        string token,
        string merchant,
        RawTokenAmount amount)
    {
        ValidateCommonArguments(paymentId, token, merchant, amount);

        var message = new PayFunction
        {
            PaymentId = paymentId.ToBytes(),
            Token = NormalizeNonZeroAddress(token, nameof(token)),
            Merchant = NormalizeNonZeroAddress(merchant, nameof(merchant)),
            Amount = amount.Value,
        };

        return Encode(message);
    }

    /// <summary>
    /// Encodes payWithPermit(...) without signing, checking a permit signature,
    /// or sending a transaction.
    /// </summary>
    public EncodedPaymentRouterCall EncodePayWithPermit(
        PaymentId paymentId,
        string token,
        string merchant,
        RawTokenAmount amount,
        BigInteger permitDeadline,
        byte v,
        ReadOnlySpan<byte> r,
        ReadOnlySpan<byte> s)
    {
        ValidateCommonArguments(paymentId, token, merchant, amount);
        ValidateUint256(permitDeadline, nameof(permitDeadline));

        var message = new PayWithPermitFunction
        {
            PaymentId = paymentId.ToBytes(),
            Token = NormalizeNonZeroAddress(token, nameof(token)),
            Merchant = NormalizeNonZeroAddress(merchant, nameof(merchant)),
            Amount = amount.Value,
            PermitDeadline = permitDeadline,
            V = v,
            R = CopyBytes32(r, nameof(r)),
            S = CopyBytes32(s, nameof(s)),
        };

        return Encode(message);
    }

    private EncodedPaymentRouterCall Encode<TFunction>(TFunction message)
        where TFunction : Nethereum.Contracts.CQS.ContractMessageBase
    {
        // A fresh encoder avoids sharing mutable FunctionBuilder state between
        // concurrent requests. GetCallData is purely local and performs no RPC.
        var encoder = new FunctionMessageEncodingService<TFunction>();
        byte[] data = encoder.GetCallData(message);

        return new EncodedPaymentRouterCall(
            Identity.ContractAddress,
            $"0x{Convert.ToHexString(data).ToLowerInvariant()}");
    }

    private void ValidateCommonArguments(
        PaymentId paymentId,
        string token,
        string merchant,
        RawTokenAmount amount)
    {
        ArgumentNullException.ThrowIfNull(paymentId);
        string normalizedMerchant = NormalizeNonZeroAddress(merchant, nameof(merchant));
        _ = NormalizeNonZeroAddress(token, nameof(token));

        // The stateless Router has no withdrawal path, so paying itself would
        // create stuck custody. Mirror the contract's fail-fast precondition.
        if (AddressUtil.Current.AreAddressesTheSame(
                normalizedMerchant,
                Identity.ContractAddress))
        {
            throw new ArgumentException(
                "The merchant cannot be the PaymentRouter contract itself.",
                nameof(merchant));
        }

        if (amount.Value == BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A payment amount must be greater than zero.");
        }
    }

    private static string NormalizeNonZeroAddress(string address, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address, parameterName);

        string candidate = address.StartsWith("0X", StringComparison.Ordinal)
            ? $"0x{address[2..]}"
            : address;

        AddressUtil addressUtil = AddressUtil.Current;
        if (!addressUtil.IsValidEthereumAddressHexFormat(candidate) ||
            addressUtil.IsZeroAddress(candidate))
        {
            throw new ArgumentException(
                "The value must be a non-zero 20-byte Ethereum address.",
                parameterName);
        }

        return candidate.ToLowerInvariant();
    }

    private static void ValidateUint256(BigInteger value, string parameterName)
    {
        if (value < BigInteger.Zero || value > RawTokenAmount.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The value must fit in an unsigned EVM uint256.");
        }
    }

    private static byte[] CopyBytes32(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException(
                "The ECDSA component must contain exactly 32 bytes.",
                parameterName);
        }

        return value.ToArray();
    }
}
