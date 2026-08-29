using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;

namespace PaymentSandbox.Finality.Evaluation;

/// <summary>Projects caught-up canonical and Ledger snapshots into reversible decisions.</summary>
public sealed class ConfirmationFinalityProcessor
{
    private readonly ConfirmationFinalityPolicy _policy;
    private readonly IChainObservationReader _observations;
    private readonly ILedgerEntryReader _ledger;
    private readonly IFinalityStore _store;
    private readonly TimeProvider _timeProvider;

    public ConfirmationFinalityProcessor(
        ConfirmationFinalityPolicy policy,
        IChainObservationReader observations,
        ILedgerEntryReader ledger,
        IFinalityStore store,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Evaluates one explicit Ledger high-watermark against one exact Indexer snapshot.
    /// </summary>
    public async Task<FinalityEvaluationResult> EvaluateAsync(
        long throughLedgerEntryId,
        ChainObservationSnapshot expectedObservationSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughLedgerEntryId);
        ArgumentNullException.ThrowIfNull(expectedObservationSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ChainObservationCheckpoint expectedHead = expectedObservationSnapshot.Checkpoint ??
            throw new ArgumentException(
                "Finality evaluation requires an Indexer stream head.",
                nameof(expectedObservationSnapshot));
        ValidateHeadStream(expectedHead);

        FinalityCheckpoint? previous = await _store.GetCheckpointAsync(
            _policy.ChainId,
            _policy.Router,
            cancellationToken);
        ValidateStoredCheckpoint(previous);
        // This is an explicit-snapshot no-op, not a "nothing happened lately"
        // guess. Matching every selected source coordinate lets us return before
        // touching either upstream database, which tests can verify directly.
        if (previous is not null &&
            previous.LastLedgerEntryId == throughLedgerEntryId &&
            previous.LastIndexerTransitionId ==
                expectedObservationSnapshot.CanonicalityHighWatermark &&
            previous.HeadBlockNumber == expectedHead.LastBlockNumber &&
            previous.HeadBlockHash == expectedHead.LastBlockHash &&
            previous.HeadCheckpointRevision == expectedHead.Revision)
        {
            return new FinalityEvaluationResult(
                FinalityEvaluationDisposition.NoWork,
                previous,
                SourceLedgerEntryCount: 0,
                QualificationCount: 0,
                RevocationCount: 0);
        }

        long afterLedgerEntryId = previous?.LastLedgerEntryId ?? 0;
        if (throughLedgerEntryId < afterLedgerEntryId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughLedgerEntryId),
                "The Ledger source cursor cannot move backward.");
        }

        ChainObservationSnapshot currentObservation = await ReadObservationSnapshotAsync(
            cancellationToken);
        if (currentObservation != expectedObservationSnapshot)
        {
            throw new FinalityEvaluationException(
                "The Indexer canonical snapshot changed before finality evaluation.");
        }

        LedgerCheckpoint ledgerCheckpoint = await ReadLedgerCheckpointAsync(cancellationToken) ??
            throw new FinalityEvaluationException(
                "The Ledger has no source checkpoint for this stream.");
        if (ledgerCheckpoint.ChainId != _policy.ChainId ||
            ledgerCheckpoint.Router != _policy.Router)
        {
            throw new FinalityEvaluationException(
                "The Ledger checkpoint belongs to a different stream.");
        }

        // This equality is the critical cross-projection guard. A lower cursor
        // means Ledger may not yet contain the reversal for the selected head;
        // a higher cursor belongs to a later Indexer snapshot than the caller selected.
        if (ledgerCheckpoint.LastSourceTransitionId !=
            currentObservation.CanonicalityHighWatermark)
        {
            throw new FinalityEvaluationException(
                "The Ledger is not caught up to the exact Indexer canonicality snapshot.");
        }

        long ledgerHighWatermark = await ReadLedgerHighWatermarkAsync(cancellationToken);
        if (throughLedgerEntryId != ledgerHighWatermark)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughLedgerEntryId),
                $"The requested Ledger entry target {throughLedgerEntryId} must equal the committed high-watermark {ledgerHighWatermark}.");
        }

        IReadOnlyList<LedgerEntry> entries = await ReadLedgerEntriesAsync(
            afterLedgerEntryId,
            throughLedgerEntryId,
            cancellationToken);
        if (entries.Count > _policy.MaxLedgerEntriesPerEvaluation)
        {
            throw new FinalityEvaluationException(
                $"The source interval contains more than {_policy.MaxLedgerEntriesPerEvaluation} Ledger entries.");
        }

        ValidateEntries(entries, afterLedgerEntryId, throughLedgerEntryId);
        var batch = new FinalityEvaluationBatch(
            _policy,
            throughLedgerEntryId,
            ledgerCheckpoint,
            currentObservation,
            entries,
            _timeProvider.GetUtcNow());
        FinalityCommitResult committed = await _store.CommitAsync(
            previous,
            batch,
            cancellationToken);
        return new FinalityEvaluationResult(
            committed.Disposition == FinalityCommitDisposition.Applied
                ? FinalityEvaluationDisposition.Applied
                : FinalityEvaluationDisposition.Replayed,
            committed.Checkpoint,
            entries.Count,
            committed.QualificationCount,
            committed.RevocationCount);
    }

    private void ValidateHeadStream(ChainObservationCheckpoint head)
    {
        if (head.ChainId != _policy.ChainId || head.Router != _policy.Router)
        {
            throw new ArgumentException(
                "The expected Indexer head belongs to a different policy stream.",
                nameof(head));
        }
    }

    private void ValidateStoredCheckpoint(FinalityCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return;
        }

        if (checkpoint.ChainId != _policy.ChainId || checkpoint.Router != _policy.Router)
        {
            throw new InvalidOperationException(
                "The stored finality checkpoint belongs to a different stream.");
        }

        if (!string.Equals(
                checkpoint.PolicyFingerprint,
                _policy.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured finality policy differs from the durable projection policy.");
        }
    }

    private void ValidateEntries(
        IReadOnlyList<LedgerEntry> entries,
        long afterEntryId,
        long throughEntryId)
    {
        long previousEntryId = afterEntryId;
        foreach (LedgerEntry entry in entries)
        {
            if (entry.ChainId != _policy.ChainId ||
                entry.Router != _policy.Router ||
                entry.EntryId <= previousEntryId ||
                entry.EntryId > throughEntryId)
            {
                throw new FinalityEvaluationException(
                    "The Ledger returned an entry outside the requested stream interval.");
            }

            previousEntryId = entry.EntryId;
        }
    }

    private async Task<ChainObservationSnapshot> ReadObservationSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _observations.GetCanonicalSnapshotAsync(
                _policy.ChainId,
                _policy.Router,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FinalityEvaluationException(
                "Reading the Indexer canonical snapshot failed.",
                exception);
        }
    }

    private async Task<LedgerCheckpoint?> ReadLedgerCheckpointAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _ledger.GetCheckpointAsync(
                _policy.ChainId,
                _policy.Router,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FinalityEvaluationException("Reading the Ledger checkpoint failed.", exception);
        }
    }

    private async Task<long> ReadLedgerHighWatermarkAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _ledger.GetEntryHighWatermarkAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FinalityEvaluationException(
                "Reading the Ledger entry high-watermark failed.",
                exception);
        }
    }

    private async Task<IReadOnlyList<LedgerEntry>> ReadLedgerEntriesAsync(
        long afterEntryId,
        long throughEntryId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _ledger.GetEntriesAsync(
                _policy.ChainId,
                _policy.Router,
                afterEntryId,
                throughEntryId,
                checked(_policy.MaxLedgerEntriesPerEvaluation + 1),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FinalityEvaluationException("Reading Ledger entries failed.", exception);
        }
    }
}
