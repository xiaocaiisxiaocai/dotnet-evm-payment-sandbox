using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Lifecycle;

internal static class TransactionLifecycleFingerprint
{
    internal static string ForRequest(
        TransactionLifecyclePolicy policy,
        PaymentTransactionRequest request,
        string calldata)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "payment-sandbox/transaction-operation-request/v1");
        Append(hash, policy.Fingerprint);
        Append(hash, request.OperationId.Value);
        Append(hash, request.PaymentId.Value);
        Append(hash, request.Token.Value);
        Append(hash, request.Merchant.Value);
        Append(hash, request.Amount.ToString());
        Append(hash, request.GasLimit);
        Append(hash, request.InitialFee.MaxFeePerGasWei);
        Append(hash, request.InitialFee.MaxPriorityFeePerGasWei);
        Append(hash, calldata);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string ForUnsigned(UnsignedPaymentTransaction value)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "payment-sandbox/unsigned-payment-transaction/v1");
        Append(hash, value.ChainId.ToString());
        Append(hash, value.Signer.Value);
        Append(hash, value.Destination.Value);
        Append(hash, value.Nonce);
        Append(hash, value.GasLimit);
        Append(hash, value.MaxFeePerGasWei);
        Append(hash, value.MaxPriorityFeePerGasWei);
        Append(hash, value.Data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, BigInteger value) =>
        Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}
