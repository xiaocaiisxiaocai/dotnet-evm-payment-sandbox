namespace PaymentSandbox.Indexer.Chain;

/// <summary>An atomically read Indexer checkpoint and transition high-watermark.</summary>
/// <remarks>
/// The transition watermark is global to the Indexer database. The checkpoint
/// identifies one chain/Router stream. Reading them together prevents a
/// downstream projection from combining a head with an unrelated later cursor.
/// </remarks>
public sealed record ChainObservationSnapshot
{
    public ChainObservationSnapshot(
        ChainObservationCheckpoint? checkpoint,
        long canonicalityHighWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalityHighWatermark);
        if (checkpoint is not null && canonicalityHighWatermark == 0)
        {
            throw new ArgumentException(
                "A checkpoint cannot exist without canonicality history.",
                nameof(canonicalityHighWatermark));
        }

        Checkpoint = checkpoint;
        CanonicalityHighWatermark = canonicalityHighWatermark;
    }

    public ChainObservationCheckpoint? Checkpoint { get; }

    public long CanonicalityHighWatermark { get; }
}
