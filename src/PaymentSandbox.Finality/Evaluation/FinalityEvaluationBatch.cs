using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Finality.Evaluation;

/// <summary>Exact cross-database facts prepared for one finality evaluation.</summary>
public sealed record FinalityEvaluationBatch
{
    public FinalityEvaluationBatch(
        ConfirmationFinalityPolicy policy,
        long throughLedgerEntryId,
        LedgerCheckpoint ledgerCheckpoint,
        ChainObservationSnapshot observationSnapshot,
        IReadOnlyList<LedgerEntry> newLedgerEntries,
        DateTimeOffset recordedAtUtc)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentOutOfRangeException.ThrowIfNegative(throughLedgerEntryId);
        LedgerCheckpoint = ledgerCheckpoint ??
            throw new ArgumentNullException(nameof(ledgerCheckpoint));
        ObservationSnapshot = observationSnapshot ??
            throw new ArgumentNullException(nameof(observationSnapshot));
        ChainObservationCheckpoint head = observationSnapshot.Checkpoint ??
            throw new ArgumentException(
                "Finality cannot be evaluated without an Indexer stream head.",
                nameof(observationSnapshot));
        if (ledgerCheckpoint.ChainId != policy.ChainId ||
            ledgerCheckpoint.Router != policy.Router ||
            head.ChainId != policy.ChainId ||
            head.Router != policy.Router)
        {
            throw new ArgumentException("Every source snapshot must belong to the policy stream.");
        }

        if (ledgerCheckpoint.LastSourceTransitionId !=
            observationSnapshot.CanonicalityHighWatermark)
        {
            throw new ArgumentException(
                "The Ledger must consume the exact Indexer transition snapshot before finality evaluation.",
                nameof(ledgerCheckpoint));
        }

        ArgumentNullException.ThrowIfNull(newLedgerEntries);
        LedgerEntry[] entries = newLedgerEntries.ToArray();
        long previousEntryId = 0;
        foreach (LedgerEntry entry in entries)
        {
            if (entry.ChainId != policy.ChainId || entry.Router != policy.Router)
            {
                throw new ArgumentException(
                    "Every ledger entry must belong to the policy stream.",
                    nameof(newLedgerEntries));
            }

            if (entry.EntryId <= previousEntryId || entry.EntryId > throughLedgerEntryId)
            {
                throw new ArgumentException(
                    "Ledger entries must be strictly ordered within the selected target.",
                    nameof(newLedgerEntries));
            }

            previousEntryId = entry.EntryId;
        }

        ThroughLedgerEntryId = throughLedgerEntryId;
        NewLedgerEntries = Array.AsReadOnly(entries);
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        Fingerprint = ComputeFingerprint(
            policy,
            throughLedgerEntryId,
            ledgerCheckpoint,
            observationSnapshot,
            entries);
    }

    public ConfirmationFinalityPolicy Policy { get; }
    public long ThroughLedgerEntryId { get; }
    public LedgerCheckpoint LedgerCheckpoint { get; }
    public ChainObservationSnapshot ObservationSnapshot { get; }
    public IReadOnlyList<LedgerEntry> NewLedgerEntries { get; }
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>SHA-256 over policy and source facts; local recording time is excluded.</summary>
    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        ConfirmationFinalityPolicy policy,
        long throughLedgerEntryId,
        LedgerCheckpoint ledgerCheckpoint,
        ChainObservationSnapshot observationSnapshot,
        IReadOnlyList<LedgerEntry> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "payment-sandbox/finality-evaluation-batch/v1");
        AppendString(hash, policy.Fingerprint);
        AppendInt64(hash, throughLedgerEntryId);
        AppendInt64(hash, ledgerCheckpoint.LastSourceTransitionId);
        AppendInt64(hash, ledgerCheckpoint.Revision);
        AppendString(hash, ledgerCheckpoint.LastBatchFingerprint);
        AppendString(hash, ledgerCheckpoint.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        ChainObservationCheckpoint head = observationSnapshot.Checkpoint!;
        AppendInt64(hash, observationSnapshot.CanonicalityHighWatermark);
        AppendInt64(hash, head.StartBlockNumber);
        AppendInt64(hash, head.LastBlockNumber);
        AppendString(hash, head.LastBlockHash.Value);
        AppendInt64(hash, head.Revision);
        AppendString(hash, head.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        AppendInt64(hash, entries.Count);
        foreach (LedgerEntry entry in entries)
        {
            AppendInt64(hash, entry.EntryId);
            AppendInt64(hash, (long)entry.Kind);
            AppendInt64(hash, entry.SourceTransitionId);
            AppendInt64(hash, entry.SourceCheckpointRevision);
            AppendInt64(hash, entry.BlockNumber);
            AppendString(hash, entry.BlockHash.Value);
            AppendString(hash, entry.TransactionHash.Value);
            AppendInt64(hash, entry.LogIndex);
            AppendString(hash, entry.PaymentId.Value);
            AppendString(hash, entry.Payer.Value);
            AppendString(hash, entry.Token.Value);
            AppendString(hash, entry.Merchant.Value);
            AppendString(hash, entry.Amount.ToString());
            AppendInt64(hash, entry.ReversesEntryId ?? 0);
            AppendString(hash, entry.SourceChangedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            AppendString(hash, entry.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt64(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}
