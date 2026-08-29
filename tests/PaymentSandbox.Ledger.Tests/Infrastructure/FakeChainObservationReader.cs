using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;

namespace PaymentSandbox.Ledger.Tests.Infrastructure;

internal sealed class FakeChainObservationReader : IChainObservationReader
{
    private readonly IReadOnlyList<BlockCanonicalityTransition> _transitions;
    private readonly IReadOnlyDictionary<(long BlockNumber, string BlockHash),
        IReadOnlyList<PaymentRecordedObservation>> _payments;

    internal FakeChainObservationReader(
        long highWatermark,
        IReadOnlyList<BlockCanonicalityTransition>? transitions = null,
        IReadOnlyDictionary<(long, string), IReadOnlyList<PaymentRecordedObservation>>? payments = null)
    {
        HighWatermark = highWatermark;
        _transitions = transitions ?? [];
        _payments = payments ??
            new Dictionary<(long, string), IReadOnlyList<PaymentRecordedObservation>>();
    }

    internal long HighWatermark { get; set; }

    internal Exception? HighWatermarkException { get; set; }

    internal int HighWatermarkReads { get; private set; }

    internal int TransitionReads { get; private set; }

    internal int PaymentReads { get; private set; }

    public ValueTask<ChainObservationSnapshot> GetCanonicalSnapshotAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ChainObservationSnapshot(null, HighWatermark));
    }

    public ValueTask<long> GetCanonicalityHighWatermarkAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HighWatermarkReads++;
        return HighWatermarkException is null
            ? ValueTask.FromResult(HighWatermark)
            : ValueTask.FromException<long>(HighWatermarkException);
    }

    public ValueTask<IReadOnlyList<BlockCanonicalityTransition>>
        GetCanonicalityTransitionsAsync(
            EvmChainId chainId,
            EvmAddress router,
            long afterTransitionId,
            long throughTransitionId,
            int maxCount,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransitionReads++;
        IReadOnlyList<BlockCanonicalityTransition> result = _transitions
            .Where(item => item.ChainId == chainId && item.Router == router)
            .Where(item => item.TransitionId > afterTransitionId &&
                item.TransitionId <= throughTransitionId)
            .OrderBy(item => item.TransitionId)
            .Take(maxCount)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<PaymentRecordedObservation>> GetPaymentsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PaymentReads++;
        _payments.TryGetValue((blockNumber, blockHash.Value), out var values);
        IReadOnlyList<PaymentRecordedObservation> result = (values ?? [])
            .Where(item => item.ChainId == chainId && item.Router == router)
            .Take(maxCount)
            .ToArray();
        return ValueTask.FromResult(result);
    }
}
