using System.Numerics;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Permits.Erc2612;
using PaymentSandbox.Permits.Persistence;
using PaymentSandbox.Permits.Preflight;
using PaymentSandbox.Permits.Workflow;

namespace PaymentSandbox.Permits.Tests.Infrastructure;

internal static class PermitWorkflowTestData
{
    internal const string RouterAddress = "0x1111111111111111111111111111111111111111";
    internal const string TokenAddress = "0x2222222222222222222222222222222222222222";
    internal const string MerchantAddress = "0x3333333333333333333333333333333333333333";
    internal const string RuntimeCode = "0x6000";
    internal const string ZeroByteCodeHash =
        "0xbc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a";
    internal static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    internal static Erc2612PermitPolicy PermitPolicy() => new(
        new EvmChainId(31_337),
        EvmAddress.Parse(TokenAddress),
        "Test USDC",
        "1",
        EvmAddress.Parse(RouterAddress),
        TimeSpan.FromMinutes(10));

    internal static Erc2612TokenTrustPolicy TrustPolicy()
    {
        string hash = $"0x{Convert.ToHexStringLower(
            Sha3Keccack.Current.CalculateHash(Convert.FromHexString(RuntimeCode.AsSpan(2))))}";
        return new Erc2612TokenTrustPolicy(PermitPolicy(), hash);
    }

    internal static async Task<VerifiedPaymentRouterClient> RouterAsync()
    {
        var connector = new PaymentRouterConnector(new FixedRouterIdentityRpc());
        return await connector.ConnectAsync(
            new PaymentRouterTrustPolicy(31_337, RouterAddress, ZeroByteCodeHash),
            TestContext.Current.CancellationToken);
    }

    internal static async Task<WorkflowFixture> CreateWorkflowAsync(
        TemporaryPermitDatabase temporary,
        MutableTokenSnapshotRpc? rpc = null,
        MutableTimeProvider? clock = null,
        int capacity = 1_024)
    {
        clock ??= new MutableTimeProvider(Now);
        rpc ??= new MutableTokenSnapshotRpc(TrustPolicy(), nonce: 7);
        var database = new PermitWorkflowDatabase(
            new PermitWorkflowDatabaseOptions(temporary.DatabasePath, capacity),
            clock);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var store = new SqlitePermitWorkflowStore(database);
        Erc2612PermitPolicy policy = PermitPolicy();
        var permit = new Erc2612PermitService(policy, clock);
        var preflight = new Erc2612PermitPreflightService(TrustPolicy(), rpc);
        var workflow = new Erc2612PermitWorkflow(permit, preflight, store, clock);
        return new WorkflowFixture(workflow, store, database, rpc, clock);
    }

    internal sealed record WorkflowFixture(
        Erc2612PermitWorkflow Workflow,
        SqlitePermitWorkflowStore Store,
        PermitWorkflowDatabase Database,
        MutableTokenSnapshotRpc Rpc,
        MutableTimeProvider Clock);

    internal sealed class TestWallet
    {
        private readonly EthECKey _key = EthECKey.GenerateKey();

        internal EvmAddress Address => EvmAddress.Parse(_key.GetPublicAddress());

        internal string Sign(Erc2612PermitDraft draft) =>
            Eip712TypedDataSigner.Current.SignTypedDataV4(draft.TypedDataJson, _key);
    }

    internal sealed class MutableTokenSnapshotRpc : IErc2612TokenSnapshotRpc
    {
        private readonly Erc2612TokenTrustPolicy _policy;
        private long _blockNumber = 100;
        private BigInteger _nonce;

        internal MutableTokenSnapshotRpc(Erc2612TokenTrustPolicy policy, BigInteger nonce)
        {
            _policy = policy;
            _nonce = nonce;
        }

        internal BigInteger ChainId { get; set; } = 31_337;
        internal string RuntimeCode { get; set; } = PermitWorkflowTestData.RuntimeCode;
        internal string TokenName { get; set; } = "Test USDC";
        internal string? DomainSeparatorOverride { get; set; }
        internal int Calls { get; private set; }
        internal BigInteger Nonce
        {
            get => _nonce;
            set => _nonce = value;
        }

        public Task<Erc2612TokenSnapshotObservation> ObserveAsync(
            EvmAddress token,
            EvmAddress owner,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            long block = Interlocked.Increment(ref _blockNumber);
            string blockHash = $"0x{block:x64}";
            return Task.FromResult(new Erc2612TokenSnapshotObservation(
                ChainId,
                token,
                owner,
                block,
                blockHash,
                RuntimeCode,
                TokenName,
                DomainSeparatorOverride ??
                    Erc2612PermitService.CalculateDomainSeparator(_policy.PermitPolicy),
                _nonce));
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
        internal void Advance(TimeSpan duration) => _value += duration;
    }

    private sealed class FixedRouterIdentityRpc : IPaymentRouterIdentityRpc
    {
        public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BigInteger(31_337));

        public Task<string> GetCodeAsync(
            string contractAddress,
            CancellationToken cancellationToken = default) => Task.FromResult("0x00");
    }
}

internal sealed class TemporaryPermitDatabase : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"payment-sandbox-permit-{Guid.NewGuid():N}");

    internal string DatabasePath => Path.Combine(_directory, "permits.db");

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
