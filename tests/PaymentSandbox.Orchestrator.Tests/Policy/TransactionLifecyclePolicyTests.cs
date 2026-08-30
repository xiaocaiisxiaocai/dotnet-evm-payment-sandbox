using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Tests.Infrastructure;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Tests.Policy;

public sealed class TransactionLifecyclePolicyTests
{
    [Fact]
    public void Policy_RejectsUnapprovedChainsAndZeroAddresses()
    {
        Assert.Throws<ArgumentException>(() => new TransactionLifecyclePolicy(
            new EvmChainId(1), OrchestratorTestData.Router,
            OrchestratorTestData.Signer, "mainnet"));
        Assert.Throws<ArgumentException>(() => new TransactionLifecyclePolicy(
            new EvmChainId(8453), OrchestratorTestData.Router,
            OrchestratorTestData.Signer, "base-mainnet"));
        Assert.Throws<ArgumentException>(() => new TransactionLifecyclePolicy(
            OrchestratorTestData.ChainId,
            EvmAddress.Parse("0x0000000000000000000000000000000000000000"),
            OrchestratorTestData.Signer, "zero-router"));
    }

    [Fact]
    public void Policy_ExplicitlyAllowsSepolia()
    {
        var policy = new TransactionLifecyclePolicy(
            new EvmChainId(TransactionLifecyclePolicy.SepoliaChainId),
            OrchestratorTestData.Router, OrchestratorTestData.Signer, "sepolia-test");

        Assert.Equal(TransactionLifecyclePolicy.SepoliaChainId, policy.ChainId.Value);
    }

    [Fact]
    public void Replacement_RequiresBothFeeFieldsToMeetRoundedBump()
    {
        TransactionLifecyclePolicy policy = OrchestratorTestData.Policy();
        var previous = new TransactionFeeQuote(101, 11);

        Assert.Throws<ArgumentException>(() => policy.ValidateReplacement(
            previous, new TransactionFeeQuote(111, 13)));
        policy.ValidateReplacement(previous, new TransactionFeeQuote(112, 13));
    }

    [Fact]
    public void InitialRequest_RejectsRouterMerchantAndFeeCapOverflow()
    {
        TransactionLifecyclePolicy policy = OrchestratorTestData.Policy();
        PaymentTransactionRequest original = OrchestratorTestData.Request();
        var request = new PaymentTransactionRequest(
            original.OperationId, original.PaymentId, original.Token,
            OrchestratorTestData.Router, original.Amount, original.GasLimit,
            original.InitialFee);
        Assert.Throws<ArgumentException>(() => policy.ValidateInitialRequest(request));

        request = OrchestratorTestData.Request(maxFee: 10_001);
        Assert.Throws<ArgumentException>(() => policy.ValidateInitialRequest(request));
    }

    [Fact]
    public void Policy_RejectsFeeCapsOutsideUint256()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransactionLifecyclePolicy(
            OrchestratorTestData.ChainId, OrchestratorTestData.Router,
            OrchestratorTestData.Signer, "invalid-fee-cap",
            maxFeePerGasWei: RawTokenAmount.MaxValue + 1));
    }
}
