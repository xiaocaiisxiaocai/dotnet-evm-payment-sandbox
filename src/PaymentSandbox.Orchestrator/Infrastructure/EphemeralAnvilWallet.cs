using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Model;
using Nethereum.Signer;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>A process-local disposable wallet generated only for Anvil lifecycle tests.</summary>
/// <remarks>
/// The private key is never returned. Dispose performs a best-effort zero of the
/// byte array owned by this object; managed/runtime copies cannot be guaranteed
/// absent, so this remains unsuitable for real funds or long-lived custody.
/// </remarks>
public sealed class EphemeralAnvilWallet : IDisposable
{
    private const string PaySelector = "0x76bbf425";
    private readonly object _gate = new();
    private readonly byte[] _privateKey;
    private bool _disposed;

    private EphemeralAnvilWallet(byte[] privateKey, EvmAddress address)
    {
        _privateKey = privateKey;
        Address = address;
    }

    public EvmAddress Address { get; }

    /// <summary>Generates a fresh key from the operating-system CSPRNG.</summary>
    public static EphemeralAnvilWallet Generate()
    {
        // Almost every 256-bit value is a valid secp256k1 scalar. Retry without
        // exposing candidate bytes if the library rejects the negligible edge.
        while (true)
        {
            byte[] candidate = RandomNumberGenerator.GetBytes(32);
            try
            {
                var key = new EthECKey(candidate, isPrivate: true);
                return new EphemeralAnvilWallet(
                    candidate, EvmAddress.Parse(key.GetPublicAddress()));
            }
            catch (ArgumentException)
            {
                CryptographicOperations.ZeroMemory(candidate);
            }
        }
    }

    /// <summary>Binds this key to one reviewed Anvil payment policy.</summary>
    public ITestTransactionSigner Bind(TransactionLifecyclePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ThrowIfDisposed();
        if (policy.ChainId.Value != TransactionLifecyclePolicy.LocalAnvilChainId ||
            policy.Signer != Address)
        {
            throw new ArgumentException(
                "The ephemeral Anvil wallet must match an Anvil policy signer.",
                nameof(policy));
        }

        return new BoundSigner(this, policy);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_privateKey);
            _disposed = true;
        }
    }

    public override string ToString() => $"Ephemeral Anvil wallet {Address.Value} (key redacted)";

    private SignedTransactionPayload Sign(
        TransactionLifecyclePolicy policy,
        UnsignedPaymentTransaction transaction)
    {
        ValidateTransaction(policy, transaction);
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = new EthECKey(_privateKey, isPrivate: true);
            var unsigned = new Transaction1559(
                transaction.ChainId.Value,
                transaction.Nonce,
                transaction.MaxPriorityFeePerGasWei,
                transaction.MaxFeePerGasWei,
                transaction.GasLimit,
                transaction.Destination.Value,
                BigInteger.Zero,
                transaction.Data,
                []);
            string raw = new Transaction1559Signer().SignTransaction(key, unsigned);
            var payload = new SignedTransactionPayload(
                raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw : $"0x{raw}");
            SignedEip1559TransactionVerifier.VerifyExact(payload, transaction);
            return payload;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateTransaction(
        TransactionLifecyclePolicy policy,
        UnsignedPaymentTransaction value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool valid = value.ChainId == policy.ChainId &&
            value.Signer == policy.Signer &&
            value.Destination == policy.Router &&
            value.Nonce >= 0 && value.GasLimit is > 0 && value.GasLimit <= policy.MaxGasLimit &&
            value.MaxFeePerGasWei > BigInteger.Zero &&
            value.MaxPriorityFeePerGasWei > BigInteger.Zero &&
            value.MaxPriorityFeePerGasWei <= value.MaxFeePerGasWei &&
            value.MaxFeePerGasWei <= policy.MaxFeePerGasWei &&
            value.MaxPriorityFeePerGasWei <= policy.MaxPriorityFeePerGasWei &&
            value.Data.Length == 266 &&
            value.Data.StartsWith(PaySelector, StringComparison.Ordinal);
        if (!valid)
        {
            throw new SignedTransactionValidationException(
                "The unsigned transaction is outside the bound Anvil payment policy.");
        }
    }

    private sealed class BoundSigner(
        EphemeralAnvilWallet wallet,
        TransactionLifecyclePolicy policy) : ITestTransactionSigner
    {
        public Task<SignedTransactionPayload> SignAsync(
            UnsignedPaymentTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(wallet.Sign(policy, transaction));
        }
    }
}
