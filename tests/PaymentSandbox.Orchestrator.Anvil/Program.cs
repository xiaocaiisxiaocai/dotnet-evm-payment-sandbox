using System.Numerics;
using System.Security.Cryptography;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Infrastructure;
using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Persistence;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

return await Week14AnvilVerification.RunAsync(args);

internal static class Week14AnvilVerification
{
    private static readonly EvmChainId ChainId = new(
        TransactionLifecyclePolicy.LocalAnvilChainId);
    private static readonly BigInteger PaymentAmount = new(1_250_000);
    private static string _phase = "argument-validation";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            VerificationArguments options = VerificationArguments.Parse(args);
            await ExecuteAsync(options, CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            // The harness never prints exception messages or inner exceptions:
            // adapters and RPC libraries may include raw payloads or endpoints.
            Console.Error.WriteLine(
                $"Week 14 Anvil lifecycle verification failed in {_phase} " +
                $"({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task ExecuteAsync(
        VerificationArguments options,
        CancellationToken cancellationToken)
    {
        _phase = "ephemeral-wallet-and-rpc-connect";
        using var wallet = EphemeralAnvilWallet.Generate();
        await using LocalAnvilRpcClient rpc = await LocalAnvilRpcClient.ConnectAsync(
            new LocalAnvilRpcClientOptions(options.RpcUrl), cancellationToken);
        var connector = new PaymentRouterConnector(rpc);
        VerifiedPaymentRouterClient routerClient = await connector.ConnectAsync(
            new PaymentRouterTrustPolicy(
                ChainId.Value, options.Router.Value, options.RouterRuntimeHash),
            cancellationToken);
        var policy = new TransactionLifecyclePolicy(
            ChainId, options.Router, wallet.Address, "week14-ephemeral-anvil-v1",
            maxGasLimit: 500_000,
            maxFeePerGasWei: new BigInteger(20_000_000_000),
            maxPriorityFeePerGasWei: new BigInteger(5_000_000_000),
            minimumReplacementFeeBumpBasisPoints: 1_000,
            maxAttemptsPerOperation: 3,
            maxReservedNonceLead: 2);

        using var setup = new AnvilSetupClient(options.RpcUrl);
        bool automineDisabled = false;
        string databasePath = Path.Combine(
            Path.GetTempPath(), $"payment-sandbox-week14-{Guid.NewGuid():N}.db");
        try
        {
            _phase = "wallet-funding-mint-and-approval";
            await setup.PrepareWalletAsync(
                wallet.Address, options.Token, options.Router,
                PaymentAmount, cancellationToken);
            BigInteger merchantBefore = await setup.GetTokenBalanceAsync(
                options.Token, options.Merchant, cancellationToken);
            BigInteger walletBefore = await setup.GetTokenBalanceAsync(
                options.Token, wallet.Address, cancellationToken);
            Assert(walletBefore == PaymentAmount, "ephemeral wallet setup balance");

            _phase = "lifecycle-database-initialization";
            var database = new TransactionLifecycleDatabase(
                new TransactionLifecycleDatabaseOptions(databasePath),
                TimeProvider.System);
            await database.InitializeAsync(cancellationToken);
            var store = new SqliteTransactionLifecycleStore(database);
            var lostResponse = new LoseFirstAcceptedResponseBroadcaster(rpc);
            var processor = new TransactionLifecycleProcessor(
                policy, routerClient, rpc, wallet.Bind(policy), lostResponse,
                rpc, store, TimeProvider.System);
            PaymentTransactionRequest request = CreateRequest(options);

            // Keep the first accepted transaction pending. If Anvil mined it
            // immediately, there would be nothing left in the mempool for a
            // higher-fee transaction with the same signer nonce to replace.
            await setup.SetAutomineAsync(enabled: false, cancellationToken);
            automineDisabled = true;

            _phase = "initial-transaction-signing";
            LifecycleCommitResult created = await processor.CreateAsync(
                request, cancellationToken);
            Assert(created.Snapshot.State == TransactionLifecycleState.Signed,
                "create must stop before broadcast");

            _phase = "lost-response-broadcast";
            // The decorator submits to the real node first and only then loses
            // the response. This models the dangerous ambiguity: the caller
            // sees an error although the side effect has already happened.
            LifecycleCommitResult unknown = await processor.BroadcastAsync(
                request.OperationId, cancellationToken);
            Assert(unknown.Snapshot.State == TransactionLifecycleState.BroadcastUnknown,
                "lost response must become unknown");

            _phase = "identical-payload-retry";
            // BroadcastAsync accepts only an operation ID, so the retry cannot
            // smuggle in freshly signed bytes. The hash list below independently
            // proves both calls used one durable payload.
            LifecycleCommitResult replayed = await processor.BroadcastAsync(
                request.OperationId, cancellationToken);
            Assert(replayed.Snapshot.State == TransactionLifecycleState.Submitted,
                "same raw bytes must become submitted or already-known");
            Assert(lostResponse.PayloadHashes.Count == 2 &&
                lostResponse.PayloadHashes[0] == lostResponse.PayloadHashes[1],
                "unknown retry must reuse one transaction hash");

            _phase = "same-nonce-replacement-signing";
            // Both fee fields exceed the durable 10% bump. Every other unsigned
            // fact is rebuilt from the stored operation and must remain equal.
            LifecycleCommitResult replacement = await processor.ReplaceAsync(
                request.OperationId,
                new TransactionFeeQuote(3_000_000_000, 2_000_000_000),
                cancellationToken);
            Assert(replacement.Snapshot.State == TransactionLifecycleState.Signed,
                "replacement must persist before broadcast");
            _phase = "replacement-broadcast";
            LifecycleCommitResult replacementSent = await processor.BroadcastAsync(
                request.OperationId, cancellationToken);
            Assert(replacementSent.Snapshot.State == TransactionLifecycleState.Submitted,
                "replacement must be accepted");

            _phase = "replacement-mining-and-receipt";
            // One manual block selects the higher-fee transaction. Receipt
            // polling checks all possibly submitted same-nonce attempts.
            await setup.MineAsync(cancellationToken);
            LifecycleCommitResult mined = await WaitForReceiptAsync(
                processor, request.OperationId, cancellationToken);
            Assert(mined.Snapshot.State == TransactionLifecycleState.MinedSucceeded,
                "replacement receipt must succeed");

            _phase = "final-attempt-and-balance-assertions";
            IReadOnlyList<TransactionAttemptSummary> attempts =
                await store.GetAttemptsAsync(request.OperationId, cancellationToken);
            Assert(attempts.Count == 2, "initial plus one replacement");
            Assert(attempts[0].Nonce == attempts[1].Nonce,
                "replacement nonce must be unchanged");
            Assert(attempts[0].TransactionHash != attempts[1].TransactionHash,
                "fee replacement must have a new hash");
            Assert(mined.Snapshot.MinedTransactionHash == attempts[1].TransactionHash,
                "the higher-fee replacement must be mined");

            BigInteger merchantAfter = await setup.GetTokenBalanceAsync(
                options.Token, options.Merchant, cancellationToken);
            BigInteger walletAfter = await setup.GetTokenBalanceAsync(
                options.Token, wallet.Address, cancellationToken);
            BigInteger routerAfter = await setup.GetTokenBalanceAsync(
                options.Token, options.Router, cancellationToken);
            Assert(merchantAfter - merchantBefore == PaymentAmount,
                "merchant delta must equal payment amount");
            Assert(walletAfter.IsZero, "ephemeral payer token balance must be spent");
            Assert(routerAfter.IsZero, "Router must retain no token balance");

            Console.WriteLine("Week 14 Anvil transaction lifecycle: PASSED");
            Console.WriteLine($"  client                  : {rpc.ClientVersion}");
            Console.WriteLine($"  chainId                 : {ChainId.Value}");
            Console.WriteLine($"  ephemeralSigner         : {wallet.Address.Value}");
            Console.WriteLine($"  reservedNonce           : {mined.Snapshot.Nonce}");
            Console.WriteLine($"  initialHash             : {attempts[0].TransactionHash.Value}");
            Console.WriteLine($"  replacementHash         : {attempts[1].TransactionHash.Value}");
            Console.WriteLine($"  finalState              : {mined.Snapshot.State}");
            Console.WriteLine($"  merchantDeltaRaw        : {merchantAfter - merchantBefore}");
            Console.WriteLine("  signedRawPrinted        : false");
            Console.WriteLine("  privateKeyPrinted       : false");
            _phase = "completed";
        }
        finally
        {
            if (automineDisabled)
            {
                await setup.SetAutomineAsync(enabled: true, CancellationToken.None);
            }

            // The lifecycle database deliberately enables SQLite pooling.  A
            // process-wide test harness may clear its own pools before removing
            // this uniquely named temporary database; production code never
            // performs this operation.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static PaymentTransactionRequest CreateRequest(VerificationArguments options)
    {
        byte[] paymentIdBytes = RandomNumberGenerator.GetBytes(32);
        if (paymentIdBytes.All(value => value == 0))
        {
            paymentIdBytes[0] = 1;
        }

        return new PaymentTransactionRequest(
            TransactionOperationId.Parse($"week14-{Guid.NewGuid():N}"),
            PaymentId.Parse($"0x{Convert.ToHexStringLower(paymentIdBytes)}"),
            options.Token,
            options.Merchant,
            new RawTokenAmount(PaymentAmount),
            gasLimit: 150_000,
            new TransactionFeeQuote(2_000_000_000, 1_000_000_000));
    }

    private static async Task<LifecycleCommitResult> WaitForReceiptAsync(
        TransactionLifecycleProcessor processor,
        TransactionOperationId operationId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            LifecycleCommitResult result = await processor.RefreshReceiptAsync(
                operationId, cancellationToken);
            if (result.Snapshot.State is TransactionLifecycleState.MinedSucceeded or
                TransactionLifecycleState.MinedReverted)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException("The replacement receipt was not observed in time.");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Week 14 assertion failed: {label}.");
        }
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (string path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

internal sealed class LoseFirstAcceptedResponseBroadcaster(
    IRawTransactionBroadcaster inner) : IRawTransactionBroadcaster
{
    private bool _loseNextResponse = true;
    internal List<TransactionHash> PayloadHashes { get; } = [];

    public async Task<TransactionBroadcastOutcome> BroadcastAsync(
        EvmChainId chainId,
        SignedTransactionPayload payload,
        CancellationToken cancellationToken = default)
    {
        PayloadHashes.Add(payload.TransactionHash);
        TransactionBroadcastOutcome outcome = await inner.BroadcastAsync(
            chainId, payload, cancellationToken);
        if (_loseNextResponse)
        {
            // Throw only after the real adapter returned. Reversing this order
            // would simulate a harmless pre-send failure, not an unknown result.
            _loseNextResponse = false;
            throw new IOException("simulated response loss after node acceptance");
        }

        return outcome;
    }
}

internal sealed class AnvilSetupClient : IDisposable
{
    private readonly IWeb3 _web3;

    internal AnvilSetupClient(string rpcUrl) => _web3 = new Web3(rpcUrl);

    internal async Task PrepareWalletAsync(
        EvmAddress wallet,
        EvmAddress token,
        EvmAddress router,
        BigInteger amount,
        CancellationToken cancellationToken)
    {
        string[] accounts = await SendRpcAsync<string[]>(
            "eth_accounts", [], cancellationToken);
        if (accounts.Length == 0)
        {
            throw new InvalidOperationException("Anvil returned no unlocked setup account.");
        }

        string deployer = EvmAddress.Parse(accounts[0]).Value;
        await SendAndWaitAsync(new TransactionInput
        {
            From = deployer,
            To = wallet.Value,
            Value = new HexBigInteger(Web3.Convert.ToWei(1)),
        }, cancellationToken);
        await SendAndWaitAsync(new TransactionInput
        {
            From = deployer,
            To = token.Value,
            Data = Encode(new MintFunction { Account = wallet.Value, Amount = amount }),
        }, cancellationToken);

        // Impersonation is setup-only: it grants ERC-20 allowance without ever
        // learning or importing the generated key. Payment attempts themselves
        // are signed by EphemeralAnvilWallet and use eth_sendRawTransaction.
        await SendRpcAsync<bool>(
            "anvil_impersonateAccount", [wallet.Value], cancellationToken);
        try
        {
            await SendAndWaitAsync(new TransactionInput
            {
                From = wallet.Value,
                To = token.Value,
                Data = Encode(new ApproveFunction { Spender = router.Value, Amount = amount }),
            }, cancellationToken);
        }
        finally
        {
            await SendRpcAsync<bool>(
                "anvil_stopImpersonatingAccount", [wallet.Value], CancellationToken.None);
        }
    }

    internal Task SetAutomineAsync(bool enabled, CancellationToken cancellationToken) =>
        SendRpcAsync<object>("evm_setAutomine", [enabled], cancellationToken);

    internal Task MineAsync(CancellationToken cancellationToken) =>
        SendRpcAsync<object>("evm_mine", [], cancellationToken);

    internal Task<BigInteger> GetTokenBalanceAsync(
        EvmAddress token,
        EvmAddress account,
        CancellationToken cancellationToken) =>
        _web3.Eth.GetContractQueryHandler<BalanceOfFunction>()
            .QueryAsync<BigInteger>(token.Value, new BalanceOfFunction { Account = account.Value })
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    public void Dispose() => (_web3.Client as IDisposable)?.Dispose();

    private async Task SendAndWaitAsync(
        TransactionInput input,
        CancellationToken cancellationToken)
    {
        string hash = await _web3.Eth.Transactions.SendTransaction
            .SendRequestAsync(input)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        for (int attempt = 0; attempt < 50; attempt++)
        {
            TransactionReceipt? receipt = await _web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(hash)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (receipt is not null)
            {
                if (receipt.Status?.Value != BigInteger.One)
                {
                    throw new InvalidOperationException("An Anvil setup transaction reverted.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException("An Anvil setup receipt was not observed in time.");
    }

    private async Task<T> SendRpcAsync<T>(
        string method,
        object[] parameters,
        CancellationToken cancellationToken) =>
        await _web3.Client.SendRequestAsync<T>(method, route: null!, parameters)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    private static string Encode<T>(T message) where T : FunctionMessage =>
        $"0x{Convert.ToHexStringLower(new Nethereum.Contracts.MessageEncodingServices.FunctionMessageEncodingService<T>()
            .GetCallData(message))}";
}

internal sealed record VerificationArguments(
    string RpcUrl,
    EvmAddress Router,
    EvmAddress Token,
    EvmAddress Merchant,
    string RouterRuntimeHash)
{
    internal static VerificationArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Week 14 arguments must be named key/value pairs.");
            }

            values.Add(args[index], args[index + 1]);
        }

        return new VerificationArguments(
            Require("--rpc-url"),
            EvmAddress.Parse(Require("--router")),
            EvmAddress.Parse(Require("--token")),
            EvmAddress.Parse(Require("--merchant")),
            Require("--runtime-hash"));

        string Require(string name) => values.TryGetValue(name, out string? value)
            ? value
            : throw new ArgumentException($"Missing required argument {name}.");
    }
}

[Function("mint")]
internal sealed class MintFunction : FunctionMessage
{
    [Parameter("address", "account", 1)]
    public string Account { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 2)]
    public BigInteger Amount { get; set; }
}

[Function("approve", "bool")]
internal sealed class ApproveFunction : FunctionMessage
{
    [Parameter("address", "spender", 1)]
    public string Spender { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 2)]
    public BigInteger Amount { get; set; }
}

[Function("balanceOf", "uint256")]
internal sealed class BalanceOfFunction : FunctionMessage
{
    [Parameter("address", "account", 1)]
    public string Account { get; set; } = string.Empty;
}
