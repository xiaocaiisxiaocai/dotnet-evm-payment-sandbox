using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Reconciliation.Policy;
using PaymentSandbox.Reconciliation.Reports;

namespace PaymentSandbox.Reconciliation.Evaluation;

/// <summary>Validated source facts and their deterministic reconciliation result.</summary>
public sealed class ReconciliationEvaluation
{
    public ReconciliationEvaluation(
        ReconciliationPolicy policy,
        PaymentId paymentId,
        PaymentIntentReadSnapshot intentSnapshot,
        LedgerReadSnapshot ledgerSnapshot,
        FinalityReadSnapshot finalitySnapshot,
        IReadOnlyList<LedgerEntry> ledgerEntries,
        IReadOnlyList<FinalityTransition> finalityTransitions,
        DateTimeOffset evaluatedAtUtc)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        PaymentId = paymentId ?? throw new ArgumentNullException(nameof(paymentId));
        IntentSnapshot = intentSnapshot ?? throw new ArgumentNullException(nameof(intentSnapshot));
        LedgerSnapshot = ledgerSnapshot ?? throw new ArgumentNullException(nameof(ledgerSnapshot));
        FinalitySnapshot = finalitySnapshot ?? throw new ArgumentNullException(nameof(finalitySnapshot));
        if (intentSnapshot.PaymentId != paymentId)
        {
            throw new ArgumentException("The Intent snapshot belongs to another payment ID.", nameof(intentSnapshot));
        }

        LedgerCheckpoint = ledgerSnapshot.Checkpoint ?? throw new ArgumentException(
            "Reconciliation requires a Ledger checkpoint.", nameof(ledgerSnapshot));
        FinalityCheckpoint = finalitySnapshot.Checkpoint ?? throw new ArgumentException(
            "Reconciliation requires a Finality checkpoint.", nameof(finalitySnapshot));
        ValidateSnapshotAlignment(policy, ledgerSnapshot, FinalityCheckpoint);

        LedgerEntries = ledgerEntries?.ToArray()
            ?? throw new ArgumentNullException(nameof(ledgerEntries));
        FinalityTransitions = finalityTransitions?.ToArray()
            ?? throw new ArgumentNullException(nameof(finalityTransitions));
        ValidateFacts(policy, paymentId, ledgerSnapshot, finalitySnapshot, LedgerEntries, FinalityTransitions);
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();

        (CanonicalOccurrenceCount, ActiveOccurrenceCount, MatchingActiveOccurrenceCount,
            QualifiedMatchingOccurrenceCount, MatchingActiveAmount, QualifiedMatchingAmount,
            Discrepancies) = Classify(policy, intentSnapshot.Intent, LedgerEntries, FinalityTransitions);
        IsConsistent = Discrepancies.Count == 0;
        BatchFingerprint = ComputeFingerprint(this);
    }

    public ReconciliationPolicy Policy { get; }
    public PaymentId PaymentId { get; }
    public PaymentIntentReadSnapshot IntentSnapshot { get; }
    public LedgerReadSnapshot LedgerSnapshot { get; }
    public FinalityReadSnapshot FinalitySnapshot { get; }
    public LedgerCheckpoint LedgerCheckpoint { get; }
    public FinalityCheckpoint FinalityCheckpoint { get; }
    public IReadOnlyList<LedgerEntry> LedgerEntries { get; }
    public IReadOnlyList<FinalityTransition> FinalityTransitions { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public int CanonicalOccurrenceCount { get; }
    public int ActiveOccurrenceCount { get; }
    public int MatchingActiveOccurrenceCount { get; }
    public int QualifiedMatchingOccurrenceCount { get; }
    public BigInteger MatchingActiveAmount { get; }
    public BigInteger QualifiedMatchingAmount { get; }
    public IReadOnlyList<ReconciliationDiscrepancyCode> Discrepancies { get; }
    public bool IsConsistent { get; }
    public string BatchFingerprint { get; }

    private static void ValidateSnapshotAlignment(
        ReconciliationPolicy policy,
        LedgerReadSnapshot ledger,
        FinalityCheckpoint finality)
    {
        LedgerCheckpoint checkpoint = ledger.Checkpoint!;
        if (checkpoint.ChainId != policy.ChainId || checkpoint.Router != policy.Router ||
            finality.ChainId != policy.ChainId || finality.Router != policy.Router)
        {
            throw new ArgumentException("Source checkpoints belong to another reconciliation stream.");
        }

        if (finality.LastLedgerEntryId != ledger.EntryHighWatermark ||
            finality.LedgerCheckpointRevision != checkpoint.Revision ||
            finality.LastIndexerTransitionId != checkpoint.LastSourceTransitionId)
        {
            throw new ArgumentException(
                "Finality must be caught up to the exact selected Ledger snapshot.");
        }
    }

    private static void ValidateFacts(
        ReconciliationPolicy policy,
        PaymentId paymentId,
        LedgerReadSnapshot ledgerSnapshot,
        FinalityReadSnapshot finalitySnapshot,
        IReadOnlyList<LedgerEntry> entries,
        IReadOnlyList<FinalityTransition> transitions)
    {
        // Readers are trust boundaries. Revalidate the append-only state
        // machine here so a faulty alternative reader cannot persist a report
        // from malformed reversal or qualification history.
        long previousEntryId = 0;
        var effects = new Dictionary<long, LedgerEntry>();
        var activeEffects = new HashSet<long>();
        foreach (LedgerEntry entry in entries)
        {
            if (entry.EntryId <= previousEntryId || entry.EntryId > ledgerSnapshot.EntryHighWatermark ||
                entry.ChainId != policy.ChainId || entry.Router != policy.Router ||
                entry.PaymentId != paymentId || entry.SourceTransitionId <= 0 ||
                entry.SourceCheckpointRevision <= 0 || entry.BlockNumber < 0 || entry.LogIndex < 0)
            {
                throw new ArgumentException("Ledger entries are not an ordered payment snapshot.", nameof(entries));
            }

            switch (entry.Kind)
            {
                case LedgerEntryKind.CanonicalPayment when entry.ReversesEntryId is null:
                    effects.Add(entry.EntryId, entry);
                    activeEffects.Add(entry.EntryId);
                    break;
                case LedgerEntryKind.CanonicalPayment:
                    throw new ArgumentException(
                        "A canonical Ledger effect cannot reverse another entry.", nameof(entries));
                case LedgerEntryKind.CanonicalPaymentReversal:
                    ValidateReversal(entry, effects, activeEffects, entries);
                    break;
                default:
                    throw new ArgumentException("The Ledger entry kind is unsupported.", nameof(entries));
            }

            previousEntryId = entry.EntryId;
        }

        long previousTransitionId = 0;
        var activeQualifications = new Dictionary<long, FinalityTransition>();
        foreach (FinalityTransition transition in transitions)
        {
            if (transition.TransitionId <= previousTransitionId ||
                transition.TransitionId > finalitySnapshot.TransitionHighWatermark ||
                transition.FinalityRevision <= 0 ||
                transition.FinalityRevision > finalitySnapshot.Checkpoint!.Revision ||
                transition.ChainId != policy.ChainId || transition.Router != policy.Router ||
                !effects.TryGetValue(transition.LedgerEffectEntryId, out LedgerEntry? effect) ||
                transition.HeadBlockNumber < 0 || transition.HeadCheckpointRevision <= 0 ||
                transition.HeadCheckpointRevision > finalitySnapshot.Checkpoint.HeadCheckpointRevision ||
                transition.ConfirmationCount < 0 ||
                transition.RequiredConfirmationCount != finalitySnapshot.Checkpoint.RequiredConfirmationCount ||
                transition.ConfirmationCount != CountConfirmations(
                    transition.HeadBlockNumber, effect.BlockNumber))
            {
                throw new ArgumentException("Finality transitions are not an ordered selected snapshot.", nameof(transitions));
            }

            ValidateFinalityTransition(transition, activeQualifications, transitions);
            previousTransitionId = transition.TransitionId;
        }

        if (activeQualifications.Keys.Any(effectId => !activeEffects.Contains(effectId)))
        {
            throw new ArgumentException(
                "A reversed Ledger effect cannot retain an active Finality qualification.",
                nameof(transitions));
        }
    }

    private static void ValidateReversal(
        LedgerEntry reversal,
        IReadOnlyDictionary<long, LedgerEntry> effects,
        ISet<long> activeEffects,
        IReadOnlyList<LedgerEntry> parameter)
    {
        // A reversal changes only canonical activity. Its occurrence and
        // payment terms must remain byte-for-byte equivalent to the effect.
        if (reversal.ReversesEntryId is not long effectId ||
            !effects.TryGetValue(effectId, out LedgerEntry? effect) ||
            !activeEffects.Remove(effectId) || reversal.SourceTransitionId <= effect.SourceTransitionId ||
            reversal.BlockNumber != effect.BlockNumber || reversal.BlockHash != effect.BlockHash ||
            reversal.TransactionHash != effect.TransactionHash || reversal.LogIndex != effect.LogIndex ||
            reversal.Payer != effect.Payer || reversal.Token != effect.Token ||
            reversal.Merchant != effect.Merchant || reversal.Amount != effect.Amount)
        {
            throw new ArgumentException(
                "A Ledger reversal must exactly reference one active earlier effect.", nameof(parameter));
        }
    }

    private static void ValidateFinalityTransition(
        FinalityTransition transition,
        IDictionary<long, FinalityTransition> activeQualifications,
        IReadOnlyList<FinalityTransition> parameter)
    {
        // At most one qualification generation can be active per effect. A
        // revocation must close that exact generation before another can open.
        switch (transition.Kind)
        {
            case FinalityTransitionKind.ConfirmationQualified
                when transition.RevokesTransitionId is null &&
                     transition.Reason == FinalityTransitionReason.ConfirmationThresholdReached &&
                     transition.ConfirmationCount >= transition.RequiredConfirmationCount &&
                     activeQualifications.TryAdd(transition.LedgerEffectEntryId, transition):
                return;
            case FinalityTransitionKind.ConfirmationRevoked
                when transition.RevokesTransitionId is long qualificationId &&
                     activeQualifications.TryGetValue(
                         transition.LedgerEffectEntryId, out FinalityTransition? qualification) &&
                     qualification.TransitionId == qualificationId &&
                     transition.Reason is FinalityTransitionReason.LedgerEffectReversed or
                         FinalityTransitionReason.ConfirmationThresholdLost &&
                     (transition.Reason != FinalityTransitionReason.ConfirmationThresholdLost ||
                         transition.ConfirmationCount < transition.RequiredConfirmationCount):
                activeQualifications.Remove(transition.LedgerEffectEntryId);
                return;
            default:
                throw new ArgumentException(
                    "A Finality transition must form a valid qualification/revocation history.",
                    nameof(parameter));
        }
    }

    private static long CountConfirmations(long headBlockNumber, long effectBlockNumber)
    {
        if (headBlockNumber < effectBlockNumber)
        {
            return 0;
        }

        long difference = headBlockNumber - effectBlockNumber;
        return difference == long.MaxValue ? long.MaxValue : difference + 1;
    }

    private static (int Canonical, int Active, int Matching, int Qualified,
        BigInteger MatchingAmount, BigInteger QualifiedAmount,
        IReadOnlyList<ReconciliationDiscrepancyCode> Discrepancies) Classify(
        ReconciliationPolicy policy,
        PaymentIntent? intent,
        IReadOnlyList<LedgerEntry> entries,
        IReadOnlyList<FinalityTransition> transitions)
    {
        LedgerEntry[] effects = entries.Where(item => item.Kind == LedgerEntryKind.CanonicalPayment).ToArray();
        HashSet<long> reversed = entries
            .Where(item => item.Kind == LedgerEntryKind.CanonicalPaymentReversal)
            .Select(item => item.ReversesEntryId!.Value)
            .ToHashSet();
        LedgerEntry[] active = effects.Where(item => !reversed.Contains(item.EntryId)).ToArray();
        var latest = transitions.GroupBy(item => item.LedgerEffectEntryId)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.TransitionId)!);
        var discrepancies = new HashSet<ReconciliationDiscrepancyCode>();
        if (intent is null)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.IntentMissing);
            return (effects.Length, active.Length, 0, 0, BigInteger.Zero, BigInteger.Zero,
                discrepancies.Order().ToArray());
        }

        bool chainMatches = intent.Terms.ChainId == policy.ChainId;
        if (!chainMatches)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.ChainMismatch);
        }

        if (active.Length == 0)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.ActivePaymentMissing);
            if (effects.Length > 0)
            {
                discrepancies.Add(ReconciliationDiscrepancyCode.ReversedPaymentHistory);
            }
        }

        if (active.Any(item => item.Token != intent.Terms.Token))
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.TokenMismatch);
        }

        if (active.Any(item => item.Merchant != intent.Terms.Merchant))
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.MerchantMismatch);
        }

        LedgerEntry[] matching = chainMatches
            ? active.Where(item => item.Token == intent.Terms.Token && item.Merchant == intent.Terms.Merchant).ToArray()
            : [];
        LedgerEntry[] qualified = matching.Where(item => latest.TryGetValue(item.EntryId, out FinalityTransition? value) &&
            value.Kind == FinalityTransitionKind.ConfirmationQualified).ToArray();
        BigInteger matchingAmount = matching.Aggregate(BigInteger.Zero, (sum, item) => sum + item.Amount.Value);
        BigInteger qualifiedAmount = qualified.Aggregate(BigInteger.Zero, (sum, item) => sum + item.Amount.Value);
        if (matchingAmount < intent.Terms.Amount.Value)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.AmountUnderpaid);
        }
        else if (matchingAmount > intent.Terms.Amount.Value)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.AmountOverpaid);
        }

        if (qualifiedAmount < matchingAmount)
        {
            discrepancies.Add(ReconciliationDiscrepancyCode.QualificationIncomplete);
        }

        return (effects.Length, active.Length, matching.Length, qualified.Length,
            matchingAmount, qualifiedAmount, discrepancies.Order().ToArray());
    }

    private static string ComputeFingerprint(ReconciliationEvaluation value)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "payment-sandbox/reconciliation-evaluation/v1");
        Append(hash, value.Policy.Fingerprint);
        Append(hash, value.PaymentId.Value);
        Append(hash, value.IntentSnapshot.PublicationHighWatermark);
        Append(hash, value.IntentSnapshot.PublicationId ?? 0);
        if (value.IntentSnapshot.Intent is PaymentIntent intent)
        {
            Append(hash, intent.Terms.ChainId.ToString());
            Append(hash, intent.Terms.Token.Value);
            Append(hash, intent.Terms.Merchant.Value);
            Append(hash, intent.Terms.Amount.ToString());
            Append(hash, intent.CreatedAtUtc.ToString("O"));
        }

        Append(hash, value.LedgerSnapshot.EntryHighWatermark);
        Append(hash, value.LedgerCheckpoint.LastSourceTransitionId);
        Append(hash, value.LedgerCheckpoint.Revision);
        Append(hash, value.LedgerCheckpoint.LastBatchFingerprint);
        Append(hash, value.LedgerCheckpoint.UpdatedAtUtc.ToString("O"));
        Append(hash, value.FinalitySnapshot.TransitionHighWatermark);
        Append(hash, value.FinalityCheckpoint.LastLedgerEntryId);
        Append(hash, value.FinalityCheckpoint.LedgerCheckpointRevision);
        Append(hash, value.FinalityCheckpoint.LastIndexerTransitionId);
        Append(hash, value.FinalityCheckpoint.HeadBlockNumber);
        Append(hash, value.FinalityCheckpoint.HeadBlockHash.Value);
        Append(hash, value.FinalityCheckpoint.HeadCheckpointRevision);
        Append(hash, value.FinalityCheckpoint.Revision);
        Append(hash, value.FinalityCheckpoint.PolicyId);
        Append(hash, value.FinalityCheckpoint.RequiredConfirmationCount);
        Append(hash, value.FinalityCheckpoint.PolicyFingerprint);
        Append(hash, value.FinalityCheckpoint.LastBatchFingerprint);
        Append(hash, value.FinalityCheckpoint.UpdatedAtUtc.ToString("O"));
        foreach (LedgerEntry entry in value.LedgerEntries)
        {
            Append(hash, entry.EntryId); Append(hash, entry.Kind.ToString());
            Append(hash, entry.SourceTransitionId); Append(hash, entry.SourceCheckpointRevision);
            Append(hash, entry.BlockNumber); Append(hash, entry.BlockHash.Value);
            Append(hash, entry.TransactionHash.Value); Append(hash, entry.LogIndex);
            Append(hash, entry.Payer.Value); Append(hash, entry.Token.Value);
            Append(hash, entry.Merchant.Value); Append(hash, entry.Amount.ToString());
            Append(hash, entry.ReversesEntryId ?? 0);
            Append(hash, entry.SourceChangedAtUtc.ToString("O"));
            Append(hash, entry.RecordedAtUtc.ToString("O"));
        }

        foreach (FinalityTransition transition in value.FinalityTransitions)
        {
            Append(hash, transition.TransitionId); Append(hash, transition.FinalityRevision);
            Append(hash, transition.Kind.ToString()); Append(hash, transition.LedgerEffectEntryId);
            Append(hash, transition.RevokesTransitionId ?? 0);
            Append(hash, transition.HeadBlockNumber); Append(hash, transition.HeadBlockHash.Value);
            Append(hash, transition.HeadCheckpointRevision);
            Append(hash, transition.ConfirmationCount);
            Append(hash, transition.RequiredConfirmationCount); Append(hash, transition.Reason.ToString());
            Append(hash, transition.RecordedAtUtc.ToString("O"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}
