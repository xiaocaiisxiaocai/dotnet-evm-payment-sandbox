using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;
using PaymentSandbox.Reconciliation.Persistence;
using PaymentSandbox.Reconciliation.Policy;

namespace PaymentSandbox.Reconciliation.Evaluation;

/// <summary>Builds one explainable report from exact, caught-up source snapshots.</summary>
public sealed class PaymentReconciliationProcessor
{
    private readonly ReconciliationPolicy _policy;
    private readonly IPaymentIntentReader _intents;
    private readonly ILedgerEntryReader _ledger;
    private readonly IFinalityReader _finality;
    private readonly IReconciliationStore _store;
    private readonly TimeProvider _timeProvider;

    public PaymentReconciliationProcessor(
        ReconciliationPolicy policy,
        IPaymentIntentReader intents,
        ILedgerEntryReader ledger,
        IFinalityReader finality,
        IReconciliationStore store,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _finality = finality ?? throw new ArgumentNullException(nameof(finality));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Reconciles one payment ID only when all caller-selected snapshots are
    /// still current and Finality is exactly caught up to Ledger.
    /// </summary>
    public async Task<ReconciliationCommitResult> ReconcileAsync(
        PaymentId paymentId,
        PaymentIntentReadSnapshot expectedIntent,
        LedgerReadSnapshot expectedLedger,
        FinalityReadSnapshot expectedFinality,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentId);
        ArgumentNullException.ThrowIfNull(expectedIntent);
        ArgumentNullException.ThrowIfNull(expectedLedger);
        ArgumentNullException.ThrowIfNull(expectedFinality);
        if (expectedIntent.PaymentId != paymentId)
        {
            throw new ArgumentException("The expected Intent snapshot belongs to another payment ID.", nameof(expectedIntent));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PaymentIntentReadSnapshot currentIntent = await ReadIntentAsync(paymentId, cancellationToken);
        if (currentIntent != expectedIntent)
        {
            throw new ReconciliationException("The Payment Intent snapshot changed before reconciliation.");
        }

        LedgerReadSnapshot currentLedger = await ReadLedgerAsync(cancellationToken);
        if (currentLedger != expectedLedger)
        {
            throw new ReconciliationException("The Ledger snapshot changed before reconciliation.");
        }

        FinalityReadSnapshot currentFinality = await ReadFinalityAsync(cancellationToken);
        if (currentFinality != expectedFinality)
        {
            throw new ReconciliationException("The Finality snapshot changed before reconciliation.");
        }

        // Evaluation validates all three cross-source cursor equalities again.
        // Read max+1 so a hard limit can never silently truncate evidence.
        IReadOnlyList<LedgerEntry> entries = await ReadLedgerEntriesAsync(
            paymentId, currentLedger.EntryHighWatermark, cancellationToken);
        if (entries.Count > _policy.MaxLedgerEntriesPerPayment)
        {
            throw new ReconciliationException("The payment has more Ledger entries than the reconciliation policy permits.");
        }

        var transitions = new List<FinalityTransition>();
        foreach (LedgerEntry effect in entries.Where(item => item.Kind == LedgerEntryKind.CanonicalPayment))
        {
            IReadOnlyList<FinalityTransition> effectTransitions = await ReadFinalityTransitionsAsync(
                effect.EntryId, currentFinality.TransitionHighWatermark, cancellationToken);
            if (effectTransitions.Count > _policy.MaxFinalityTransitionsPerEffect)
            {
                throw new ReconciliationException(
                    $"Ledger effect {effect.EntryId} has too many Finality transitions.");
            }

            transitions.AddRange(effectTransitions);
        }

        // Reads are partitioned by Ledger effect, so merge them into the one
        // global transition order required by the immutable evidence snapshot.
        transitions.Sort((left, right) => left.TransitionId.CompareTo(right.TransitionId));

        var evaluation = new ReconciliationEvaluation(
            _policy, paymentId, currentIntent, currentLedger, currentFinality,
            entries, transitions, _timeProvider.GetUtcNow());
        return await _store.CommitAsync(evaluation, cancellationToken);
    }

    private async Task<PaymentIntentReadSnapshot> ReadIntentAsync(
        PaymentId paymentId, CancellationToken cancellationToken)
    {
        try { return await _intents.GetSnapshotAsync(paymentId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new ReconciliationException("Payment Intent snapshot read failed.", exception); }
    }

    private async Task<LedgerReadSnapshot> ReadLedgerAsync(CancellationToken cancellationToken)
    {
        try { return await _ledger.GetSnapshotAsync(_policy.ChainId, _policy.Router, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new ReconciliationException("Ledger snapshot read failed.", exception); }
    }

    private async Task<FinalityReadSnapshot> ReadFinalityAsync(CancellationToken cancellationToken)
    {
        try { return await _finality.GetSnapshotAsync(_policy.ChainId, _policy.Router, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new ReconciliationException("Finality snapshot read failed.", exception); }
    }

    private async Task<IReadOnlyList<LedgerEntry>> ReadLedgerEntriesAsync(
        PaymentId paymentId,
        long throughEntryId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _ledger.GetEntriesByPaymentIdAsync(
                _policy.ChainId, _policy.Router, paymentId, throughEntryId,
                checked(_policy.MaxLedgerEntriesPerPayment + 1), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new ReconciliationException("Ledger payment evidence read failed.", exception);
        }
    }

    private async Task<IReadOnlyList<FinalityTransition>> ReadFinalityTransitionsAsync(
        long ledgerEffectEntryId,
        long throughTransitionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _finality.GetTransitionsThroughAsync(
                _policy.ChainId, _policy.Router, ledgerEffectEntryId, throughTransitionId,
                checked(_policy.MaxFinalityTransitionsPerEffect + 1), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new ReconciliationException(
                $"Finality evidence read failed for Ledger effect {ledgerEffectEntryId}.", exception);
        }
    }
}
