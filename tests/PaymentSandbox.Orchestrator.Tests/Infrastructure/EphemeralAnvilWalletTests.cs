using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Infrastructure;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Tests.Infrastructure;

public sealed class EphemeralAnvilWalletTests
{
    [Fact]
    public async Task BoundWallet_SignsAndRoundTripsEveryEip1559Field()
    {
        using var wallet = EphemeralAnvilWallet.Generate();
        TransactionLifecyclePolicy policy = CreatePolicy(wallet);
        UnsignedPaymentTransaction unsigned = CreateUnsigned(wallet, policy);

        SignedTransactionPayload payload = await wallet.Bind(policy)
            .SignAsync(unsigned, TestContext.Current.CancellationToken);

        Assert.StartsWith("0x02", payload.RawTransaction);
        SignedEip1559TransactionVerifier.VerifyExact(payload, unsigned);
        Assert.Contains(wallet.Address.Value, wallet.ToString(), StringComparison.Ordinal);
        Assert.Contains("key redacted", wallet.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(payload.RawTransaction, wallet.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_RejectsChangedExpectedFactsWithoutEchoingRawBytes()
    {
        using var wallet = EphemeralAnvilWallet.Generate();
        TransactionLifecyclePolicy policy = CreatePolicy(wallet);
        UnsignedPaymentTransaction unsigned = CreateUnsigned(wallet, policy);
        SignedTransactionPayload payload = await wallet.Bind(policy)
            .SignAsync(unsigned, TestContext.Current.CancellationToken);
        UnsignedPaymentTransaction changed = unsigned with { Nonce = unsigned.Nonce + 1 };

        SignedTransactionValidationException exception = Assert.Throws<SignedTransactionValidationException>(
            () => SignedEip1559TransactionVerifier.VerifyExact(payload, changed));

        Assert.DoesNotContain(payload.RawTransaction, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundWallet_RejectsAnotherDestinationBeforeSigning()
    {
        using var wallet = EphemeralAnvilWallet.Generate();
        TransactionLifecyclePolicy policy = CreatePolicy(wallet);
        UnsignedPaymentTransaction changed = CreateUnsigned(wallet, policy) with
        {
            Destination = OrchestratorTestData.Merchant,
        };

        await Assert.ThrowsAsync<SignedTransactionValidationException>(() =>
            wallet.Bind(policy).SignAsync(changed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposedWallet_CannotSignAgain()
    {
        var wallet = EphemeralAnvilWallet.Generate();
        TransactionLifecyclePolicy policy = CreatePolicy(wallet);
        var signer = wallet.Bind(policy);
        wallet.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            signer.SignAsync(CreateUnsigned(wallet, policy), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Wallet_RejectsSepoliaEvenThoughTheGeneralLifecyclePolicyAllowsIt()
    {
        using var wallet = EphemeralAnvilWallet.Generate();
        var sepoliaPolicy = new TransactionLifecyclePolicy(
            new EvmChainId(TransactionLifecyclePolicy.SepoliaChainId),
            OrchestratorTestData.Router,
            wallet.Address,
            "sepolia-must-not-use-ephemeral-anvil-wallet");

        Assert.Throws<ArgumentException>(() => wallet.Bind(sepoliaPolicy));
    }

    private static TransactionLifecyclePolicy CreatePolicy(EphemeralAnvilWallet wallet) =>
        new(OrchestratorTestData.ChainId, OrchestratorTestData.Router,
            wallet.Address, "ephemeral-anvil-unit-test",
            maxGasLimit: 500_000,
            maxFeePerGasWei: new BigInteger(10_000_000_000),
            maxPriorityFeePerGasWei: new BigInteger(2_000_000_000));

    private static UnsignedPaymentTransaction CreateUnsigned(
        EphemeralAnvilWallet wallet,
        TransactionLifecyclePolicy policy) =>
        new(
            policy.ChainId,
            wallet.Address,
            policy.Router,
            Nonce: 7,
            GasLimit: 150_000,
            MaxFeePerGasWei: new BigInteger(2_000_000_000),
            MaxPriorityFeePerGasWei: new BigInteger(1_000_000_000),
            Data: $"0x76bbf425{new string('0', 256)}");
}
