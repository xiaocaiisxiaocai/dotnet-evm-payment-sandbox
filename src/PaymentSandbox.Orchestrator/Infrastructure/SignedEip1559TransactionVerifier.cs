using System.Numerics;
using Nethereum.Model;
using Nethereum.Signer;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>Decodes opaque signed bytes and proves they contain the intended EIP-1559 fields.</summary>
public static class SignedEip1559TransactionVerifier
{
    /// <summary>
    /// Verifies encoding, hash, every unsigned field, an empty access list, and
    /// the recovered signer. No raw bytes are included in failure messages.
    /// </summary>
    public static void VerifyExact(
        SignedTransactionPayload payload,
        UnsignedPaymentTransaction expected)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            ISignedTransaction decodedBase = TransactionFactory.CreateTransaction(
                payload.RawTransaction);
            if (decodedBase is not Transaction1559 decoded)
            {
                throw Invalid("The signer returned a transaction that is not EIP-1559 type 2.");
            }

            // Re-encoding proves the parser consumed the complete canonical
            // payload rather than a valid prefix followed by ignored bytes.
            string reencoded = $"0x{Convert.ToHexStringLower(decoded.GetRLPEncoded())}";
            if (!string.Equals(reencoded, payload.RawTransaction, StringComparison.Ordinal))
            {
                throw Invalid("The signed transaction is not a canonical complete encoding.");
            }

            TransactionHash decodedHash = TransactionHash.Parse(
                $"0x{Convert.ToHexStringLower(decoded.Hash)}");
            EvmAddress destination = EvmAddress.Parse(decoded.ReceiverAddress);
            bool fieldsMatch = decodedHash == payload.TransactionHash &&
                decoded.ChainId == expected.ChainId.Value &&
                decoded.Nonce == expected.Nonce &&
                decoded.MaxFeePerGas == expected.MaxFeePerGasWei &&
                decoded.MaxPriorityFeePerGas == expected.MaxPriorityFeePerGasWei &&
                decoded.GasLimit == expected.GasLimit &&
                destination == expected.Destination &&
                (decoded.Amount ?? BigInteger.Zero) == BigInteger.Zero &&
                string.Equals(decoded.Data, expected.Data, StringComparison.OrdinalIgnoreCase) &&
                (decoded.AccessList?.Count ?? 0) == 0;
            if (!fieldsMatch)
            {
                throw Invalid("The signed transaction fields differ from the approved unsigned request.");
            }

            EthECKey recoveredKey = EthECKeyBuilderFromSignedTransaction.GetKey(decoded);
            EvmAddress recovered = EvmAddress.Parse(recoveredKey.GetPublicAddress());
            if (recovered != expected.Signer)
            {
                throw Invalid("The signed transaction recovered a different signer address.");
            }
        }
        catch (SignedTransactionValidationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Decoder/signature exceptions can contain library details. Keep
            // only the exception type as a bounded diagnostic code; never echo
            // its message, inner exception, data, or signed bytes.
            throw Invalid(
                $"The signed transaction could not be safely decoded and verified ({exception.GetType().Name}).");
        }
    }

    private static SignedTransactionValidationException Invalid(string message) => new(message);
}
