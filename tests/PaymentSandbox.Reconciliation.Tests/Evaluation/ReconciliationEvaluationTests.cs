using PaymentSandbox.Reconciliation.Reports;
using PaymentSandbox.Reconciliation.Tests.Infrastructure;

namespace PaymentSandbox.Reconciliation.Tests.Evaluation;

public sealed class ReconciliationEvaluationTests
{
    [Fact]
    public void ExactQualifiedPayment_IsLocallyConsistent()
    {
        var effect = ReconciliationTestData.Effect();
        var value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [ReconciliationTestData.Qualified(effect)]);

        Assert.True(value.IsConsistent);
        Assert.Empty(value.Discrepancies);
        Assert.Equal(1_250_000, value.MatchingActiveAmount);
        Assert.Equal(value.MatchingActiveAmount, value.QualifiedMatchingAmount);
    }

    [Fact]
    public void PartialUnqualifiedPayment_ExplainsBothDimensions()
    {
        var effect = ReconciliationTestData.Effect(amount: 500_000);
        var value = ReconciliationTestData.Evaluation(ReconciliationTestData.Intent(), [effect], []);

        Assert.Equal(
            [ReconciliationDiscrepancyCode.AmountUnderpaid,
                ReconciliationDiscrepancyCode.QualificationIncomplete],
            value.Discrepancies);
    }

    [Fact]
    public void SupplementalQualifiedPayments_AggregateToExactAmount()
    {
        var first = ReconciliationTestData.Effect(id: 1, amount: 500_000, logIndex: 1);
        var second = ReconciliationTestData.Effect(id: 2, amount: 750_000, logIndex: 2);
        var value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [first, second],
            [ReconciliationTestData.Qualified(first, 1), ReconciliationTestData.Qualified(second, 2)]);

        Assert.True(value.IsConsistent);
        Assert.Equal(2, value.MatchingActiveOccurrenceCount);
    }

    [Fact]
    public void ExcessAndWrongTokenRemainVisible()
    {
        var correct = ReconciliationTestData.Effect(id: 1, amount: 1_500_000);
        var wrong = ReconciliationTestData.Effect(id: 2, amount: 7,
            token: PaymentSandbox.Domain.Evm.EvmAddress.Parse("0x5555555555555555555555555555555555555555"));
        var value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [correct, wrong], [ReconciliationTestData.Qualified(correct)]);

        Assert.Contains(ReconciliationDiscrepancyCode.TokenMismatch, value.Discrepancies);
        Assert.Contains(ReconciliationDiscrepancyCode.AmountOverpaid, value.Discrepancies);
        Assert.Equal(1_500_000, value.MatchingActiveAmount);
    }

    [Fact]
    public void ReversedEffect_ExplainsMissingActivePayment()
    {
        var effect = ReconciliationTestData.Effect();
        var value = ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect, ReconciliationTestData.Reversal(effect)], []);

        Assert.Contains(ReconciliationDiscrepancyCode.ActivePaymentMissing, value.Discrepancies);
        Assert.Contains(ReconciliationDiscrepancyCode.ReversedPaymentHistory, value.Discrepancies);
        Assert.Contains(ReconciliationDiscrepancyCode.AmountUnderpaid, value.Discrepancies);
    }

    [Fact]
    public void MissingIntent_DoesNotInventExpectedTerms()
    {
        var effect = ReconciliationTestData.Effect();
        var value = ReconciliationTestData.Evaluation(null, [effect], []);

        Assert.Equal([ReconciliationDiscrepancyCode.IntentMissing], value.Discrepancies);
        Assert.Equal(0, value.MatchingActiveOccurrenceCount);
    }

    [Fact]
    public void ReversalWithChangedOccurrenceFacts_IsRejectedAtBoundary()
    {
        var effect = ReconciliationTestData.Effect();
        var malformed = ReconciliationTestData.Reversal(effect) with
        {
            Token = PaymentSandbox.Domain.Evm.EvmAddress.Parse(
                "0x5555555555555555555555555555555555555555"),
        };

        Assert.Throws<ArgumentException>(() => ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect, malformed], []));
    }

    [Fact]
    public void RevocationThatDoesNotReferenceActiveQualification_IsRejectedAtBoundary()
    {
        var effect = ReconciliationTestData.Effect();
        var qualification = ReconciliationTestData.Qualified(effect);
        var malformed = qualification with
        {
            TransitionId = 2,
            Kind = PaymentSandbox.Finality.Transitions.FinalityTransitionKind.ConfirmationRevoked,
            RevokesTransitionId = 999,
            HeadBlockNumber = 101,
            ConfirmationCount = 1,
            Reason = PaymentSandbox.Finality.Transitions.FinalityTransitionReason.ConfirmationThresholdLost,
        };

        Assert.Throws<ArgumentException>(() => ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [effect], [qualification, malformed]));
    }

    [Fact]
    public void UnorderedEvidence_IsRejectedInsteadOfSilentlyReordered()
    {
        var first = ReconciliationTestData.Effect(id: 1, amount: 500_000, logIndex: 1);
        var second = ReconciliationTestData.Effect(id: 2, amount: 750_000, logIndex: 2);

        Assert.Throws<ArgumentException>(() => ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(), [second, first], []));
    }

    [Fact]
    public void ReversedEffectWithUnrevokedQualification_IsRejectedAsUncaughtUpEvidence()
    {
        var effect = ReconciliationTestData.Effect();

        Assert.Throws<ArgumentException>(() => ReconciliationTestData.Evaluation(
            ReconciliationTestData.Intent(),
            [effect, ReconciliationTestData.Reversal(effect)],
            [ReconciliationTestData.Qualified(effect)]));
    }
}
