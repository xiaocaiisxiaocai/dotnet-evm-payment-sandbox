using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;

namespace PaymentSandbox.Ledger.Processing;

/// <summary>Projects append-only canonicality changes into reversible ledger evidence.</summary>
public sealed class CanonicalPaymentLedgerProcessor
{
    private readonly CanonicalPaymentLedgerPolicy _policy;
    private readonly IChainObservationReader _source;
    private readonly ILedgerStore _store;
    private readonly TimeProvider _timeProvider;

    public CanonicalPaymentLedgerProcessor(
        CanonicalPaymentLedgerPolicy policy,
        IChainObservationReader source,
        ILedgerStore store,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Processes source transitions through an explicit committed high-watermark.</summary>
    public async Task<LedgerProcessingResult> ProcessThroughTransitionAsync(
        long throughTransitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughTransitionId);
        cancellationToken.ThrowIfCancellationRequested();
        LedgerCheckpoint? previous = await _store.GetCheckpointAsync(
            _policy.ChainId,
            _policy.Router,
            cancellationToken);
        ValidateStoredCheckpoint(previous);

        long afterTransitionId = previous?.LastSourceTransitionId ?? 0;
        if (throughTransitionId <= afterTransitionId)
        {
            return new LedgerProcessingResult(
                LedgerProcessingDisposition.NoWork,
                previous,
                SourceTransitionCount: 0,
                CanonicalPaymentCount: 0,
                ReversalCount: 0);
        }

        long highWatermark = await ReadHighWatermarkAsync(cancellationToken);
        if (throughTransitionId > highWatermark)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughTransitionId),
                $"The requested transition {throughTransitionId} exceeds the committed source high-watermark {highWatermark}.");
        }

        IReadOnlyList<BlockCanonicalityTransition> transitions =
            await ReadTransitionsAsync(
                afterTransitionId,
                throughTransitionId,
                cancellationToken);
        if (transitions.Count > _policy.MaxTransitionsPerBatch)
        {
            throw new LedgerProcessingException(
                $"The source interval contains more than {_policy.MaxTransitionsPerBatch} transitions.");
        }

        var changes = new List<CanonicalPaymentChange>(transitions.Count);
        int paymentCount = 0;
        int canonicalPaymentCount = 0;
        int reversalCount = 0;
        foreach (BlockCanonicalityTransition transition in transitions)
        {
            ValidateTransition(transition, afterTransitionId, throughTransitionId);
            int remainingCapacity = _policy.MaxPaymentsPerBatch - paymentCount;
            int readLimit = checked(remainingCapacity + 1);
            IReadOnlyList<PaymentRecordedObservation> payments = await ReadPaymentsAsync(
                transition,
                readLimit,
                cancellationToken);
            if (payments.Count > remainingCapacity)
            {
                throw new LedgerProcessingException(
                    $"The source interval contains more than {_policy.MaxPaymentsPerBatch} payment occurrences.");
            }

            var change = new CanonicalPaymentChange(transition, payments);
            changes.Add(change);
            paymentCount = checked(paymentCount + payments.Count);
            if (transition.Canonicality == BlockCanonicality.Canonical)
            {
                canonicalPaymentCount = checked(canonicalPaymentCount + payments.Count);
            }
            else
            {
                reversalCount = checked(reversalCount + payments.Count);
            }
        }

        var batch = new CanonicalPaymentBatch(
            _policy.ChainId,
            _policy.Router,
            throughTransitionId,
            changes,
            _timeProvider.GetUtcNow());
        LedgerCommitResult committed = await _store.CommitAsync(
            previous,
            batch,
            cancellationToken);
        return new LedgerProcessingResult(
            LedgerProcessingResult.FromStore(committed.Disposition),
            committed.Checkpoint,
            transitions.Count,
            canonicalPaymentCount,
            reversalCount);
    }

    private void ValidateStoredCheckpoint(LedgerCheckpoint? checkpoint)
    {
        if (checkpoint is not null &&
            (checkpoint.ChainId != _policy.ChainId || checkpoint.Router != _policy.Router))
        {
            throw new InvalidOperationException(
                "The stored ledger checkpoint belongs to a different source stream.");
        }
    }

    private void ValidateTransition(
        BlockCanonicalityTransition transition,
        long afterTransitionId,
        long throughTransitionId)
    {
        if (transition.ChainId != _policy.ChainId ||
            transition.Router != _policy.Router ||
            transition.TransitionId <= afterTransitionId ||
            transition.TransitionId > throughTransitionId)
        {
            throw new LedgerProcessingException(
                "The source returned a canonicality transition outside the requested stream interval.");
        }
    }

    private async Task<long> ReadHighWatermarkAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _source.GetCanonicalityHighWatermarkAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LedgerProcessingException(
                "Reading the canonicality source high-watermark failed.",
                exception);
        }
    }

    private async Task<IReadOnlyList<BlockCanonicalityTransition>> ReadTransitionsAsync(
        long afterTransitionId,
        long throughTransitionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _source.GetCanonicalityTransitionsAsync(
                _policy.ChainId,
                _policy.Router,
                afterTransitionId,
                throughTransitionId,
                checked(_policy.MaxTransitionsPerBatch + 1),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LedgerProcessingException(
                "Reading canonicality transitions failed.",
                exception);
        }
    }

    private async Task<IReadOnlyList<PaymentRecordedObservation>> ReadPaymentsAsync(
        BlockCanonicalityTransition transition,
        int maxCount,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _source.GetPaymentsAsync(
                transition.ChainId,
                transition.Router,
                transition.BlockNumber,
                transition.BlockHash,
                maxCount,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LedgerProcessingException(
                $"Reading payments for transition {transition.TransitionId} failed.",
                exception);
        }
    }
}
