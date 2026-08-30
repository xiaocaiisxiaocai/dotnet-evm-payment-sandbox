using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Persistence;
using PaymentSandbox.Reconciliation.Policy;

namespace PaymentSandbox.Reconciliation.Tests.Infrastructure;

internal static class ReconciliationTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 30, 16, 0, 0, TimeSpan.Zero);
    internal static readonly EvmChainId ChainId = new(31_337);
    internal static readonly EvmAddress Router = EvmAddress.Parse("0x1111111111111111111111111111111111111111");
    internal static readonly EvmAddress Token = EvmAddress.Parse("0x2222222222222222222222222222222222222222");
    internal static readonly EvmAddress Merchant = EvmAddress.Parse("0x3333333333333333333333333333333333333333");
    internal static readonly PaymentId PaymentId = PaymentSandbox.Domain.Payments.PaymentId.Parse($"0x{new string('a', 64)}");
    internal static EvmHash Hash(char value) => EvmHash.Parse($"0x{new string(value, 64)}");
    internal static ReconciliationPolicy Policy() => new(ChainId, Router, "local-reconciliation-v1");

    internal static PaymentIntent Intent(BigInteger? amount = null, EvmAddress? token = null) =>
        PaymentIntent.Create(PaymentId,
            new PaymentIntentTerms(ChainId, token ?? Token, Merchant,
                new RawTokenAmount(amount ?? 1_250_000)), Now);

    internal static LedgerEntry Effect(
        long id = 1, BigInteger? amount = null, EvmAddress? token = null, long logIndex = 1) =>
        new(id, LedgerEntryKind.CanonicalPayment, id, 1, ChainId, Router, 101,
            Hash((char)('1' + id)), Hash((char)('a' + id)), logIndex, PaymentId,
            EvmAddress.Parse("0x4444444444444444444444444444444444444444"),
            token ?? Token, Merchant, new RawTokenAmount(amount ?? 1_250_000), null,
            Now.AddSeconds(id), Now.AddMinutes(1));

    internal static LedgerEntry Reversal(LedgerEntry effect, long id = 2) => effect with
    {
        EntryId = id,
        Kind = LedgerEntryKind.CanonicalPaymentReversal,
        SourceTransitionId = id,
        ReversesEntryId = effect.EntryId,
    };

    internal static FinalityTransition Qualified(LedgerEntry effect, long id = 1) =>
        new(id, 1, FinalityTransitionKind.ConfirmationQualified, effect.EntryId, null,
            ChainId, Router, 103, Hash('f'), 1, 3, 3,
            FinalityTransitionReason.ConfirmationThresholdReached, Now);

    internal static ReconciliationEvaluation Evaluation(
        PaymentIntent? intent,
        IReadOnlyList<LedgerEntry> entries,
        IReadOnlyList<FinalityTransition> transitions,
        DateTimeOffset? evaluatedAt = null)
    {
        long ledgerHigh = entries.Count == 0 ? 0 : entries.Max(item => item.EntryId);
        long transitionHigh = transitions.Count == 0 ? 0 : transitions.Max(item => item.TransitionId);
        var ledgerCheckpoint = new LedgerCheckpoint(ChainId, Router, 4, 1, new string('b', 64), Now);
        var finalityCheckpoint = new FinalityCheckpoint(ChainId, Router, ledgerHigh, 1, 4,
            103, Hash('f'), 1, 1, "confirmations-v1", 3,
            new string('c', 64), new string('d', 64), Now);
        return new ReconciliationEvaluation(Policy(), PaymentId,
            new PaymentIntentReadSnapshot(PaymentId, intent, intent is null ? 0 : 1, intent is null ? null : 1),
            new LedgerReadSnapshot(ledgerCheckpoint, ledgerHigh),
            new FinalityReadSnapshot(finalityCheckpoint, transitionHigh),
            entries, transitions, evaluatedAt ?? Now);
    }

    internal static ReconciliationDatabase Database(string path) =>
        new(new ReconciliationDatabaseOptions(path), new FixedTimeProvider(Now));

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
