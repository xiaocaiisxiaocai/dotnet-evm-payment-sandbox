using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Persistence;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Tests.Infrastructure;

internal static class OrchestratorTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);
    internal static readonly TimeProvider TimeProvider = new FixedTimeProvider(Now);
    internal static readonly EvmChainId ChainId = new(31_337);
    internal static readonly EvmAddress Router = EvmAddress.Parse("0x1111111111111111111111111111111111111111");
    internal static readonly EvmAddress Token = EvmAddress.Parse("0x2222222222222222222222222222222222222222");
    internal static readonly EvmAddress Merchant = EvmAddress.Parse("0x3333333333333333333333333333333333333333");
    internal static readonly EvmAddress Signer = EvmAddress.Parse("0x4444444444444444444444444444444444444444");
    internal static readonly PaymentId PaymentId = PaymentSandbox.Domain.Payments.PaymentId.Parse(
        $"0x{new string('a', 64)}");

    internal static TransactionLifecyclePolicy Policy(
        int maxAttempts = 10,
        int maxNonceLead = 100) =>
        new(ChainId, Router, Signer, "anvil-test-wallet-v1",
            maxGasLimit: 500_000,
            maxFeePerGasWei: 10_000,
            maxPriorityFeePerGasWei: 1_000,
            minimumReplacementFeeBumpBasisPoints: 1_000,
            maxAttemptsPerOperation: maxAttempts,
            maxReservedNonceLead: maxNonceLead);

    internal static PaymentTransactionRequest Request(
        string operationId = "operation-1",
        BigInteger? maxFee = null,
        BigInteger? priorityFee = null) =>
        new(TransactionOperationId.Parse(operationId), PaymentId, Token, Merchant,
            new RawTokenAmount(1_250_000), 150_000,
            new TransactionFeeQuote(maxFee ?? 100, priorityFee ?? 10));

    internal static async Task<VerifiedPaymentRouterClient> ConnectRouterAsync()
    {
        var connector = new PaymentRouterConnector(new FixedIdentityRpc());
        return await connector.ConnectAsync(
            new PaymentRouterTrustPolicy(ChainId.Value, Router.Value,
                "0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a"));
    }

    internal static async Task<(SqliteTransactionLifecycleStore Store, TransactionLifecycleProcessor Processor,
        FakeNonceReader Nonces, DeterministicSigner Signer, FakeBroadcaster Broadcaster,
        FakeReceiptReader Receipts)> CreateProcessorAsync(
        TemporaryTransactionLifecycleDatabase temporary,
        TransactionLifecyclePolicy? policy = null)
    {
        TransactionLifecycleDatabase database = temporary.Create();
        await database.InitializeAsync();
        var store = new SqliteTransactionLifecycleStore(database);
        var nonces = new FakeNonceReader(7);
        var signer = new DeterministicSigner();
        var broadcaster = new FakeBroadcaster();
        var receipts = new FakeReceiptReader();
        var processor = new TransactionLifecycleProcessor(
            policy ?? Policy(), await ConnectRouterAsync(), nonces, signer,
            broadcaster, receipts, store, TimeProvider);
        return (store, processor, nonces, signer, broadcaster, receipts);
    }

    internal sealed class FakeNonceReader(long value) : IAccountNonceReader
    {
        internal int Calls { get; private set; }
        internal Exception? Failure { get; set; }
        internal long Value { get; set; } = value;

        public Task<long> GetPendingNonceAsync(
            EvmChainId chainId,
            EvmAddress account,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null ? Task.FromResult(Value) : Task.FromException<long>(Failure);
        }
    }

    internal sealed class DeterministicSigner : ITestTransactionSigner
    {
        internal List<UnsignedPaymentTransaction> Transactions { get; } = [];
        internal Exception? Failure { get; set; }

        public Task<SignedTransactionPayload> SignAsync(
            UnsignedPaymentTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Transactions.Add(transaction);
            if (Failure is not null)
            {
                return Task.FromException<SignedTransactionPayload>(Failure);
            }

            string source = string.Join('|', transaction.ChainId, transaction.Signer,
                transaction.Destination, transaction.Nonce, transaction.GasLimit,
                transaction.MaxFeePerGasWei, transaction.MaxPriorityFeePerGasWei,
                transaction.Data);
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            return Task.FromResult(new SignedTransactionPayload(
                $"0x{Convert.ToHexStringLower(bytes)}"));
        }
    }

    internal sealed class FakeBroadcaster : IRawTransactionBroadcaster
    {
        internal Queue<object> Results { get; } = [];
        internal List<string> RawTransactions { get; } = [];

        public Task<TransactionBroadcastOutcome> BroadcastAsync(
            EvmChainId chainId,
            SignedTransactionPayload payload,
            CancellationToken cancellationToken = default)
        {
            RawTransactions.Add(payload.RawTransaction);
            object result = Results.Count == 0
                ? new TransactionBroadcastOutcome(TransactionBroadcastOutcomeKind.Accepted, "accepted")
                : Results.Dequeue();
            return result is Exception exception
                ? Task.FromException<TransactionBroadcastOutcome>(exception)
                : Task.FromResult((TransactionBroadcastOutcome)result);
        }
    }

    internal sealed class FakeReceiptReader : ITransactionReceiptReader
    {
        internal Dictionary<TransactionHash, TransactionReceiptObservation?> Values { get; } = [];
        internal List<TransactionHash> Reads { get; } = [];

        public Task<TransactionReceiptObservation?> GetReceiptAsync(
            EvmChainId chainId,
            TransactionHash transactionHash,
            CancellationToken cancellationToken = default)
        {
            Reads.Add(transactionHash);
            return Task.FromResult(Values.GetValueOrDefault(transactionHash));
        }
    }

    private sealed class FixedIdentityRpc : IPaymentRouterIdentityRpc
    {
        public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ChainId.Value);

        public Task<string> GetCodeAsync(
            string contractAddress,
            CancellationToken cancellationToken = default) => Task.FromResult("0x00");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
