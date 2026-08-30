namespace PaymentSandbox.Finality.Persistence;

/// <summary>A Finality stream checkpoint and global transition watermark read atomically.</summary>
public sealed record FinalityReadSnapshot
{
    public FinalityReadSnapshot(FinalityCheckpoint? checkpoint, long transitionHighWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(transitionHighWatermark);
        Checkpoint = checkpoint;
        TransitionHighWatermark = transitionHighWatermark;
    }

    public FinalityCheckpoint? Checkpoint { get; }
    public long TransitionHighWatermark { get; }
}
