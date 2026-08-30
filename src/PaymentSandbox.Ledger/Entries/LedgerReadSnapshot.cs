namespace PaymentSandbox.Ledger.Entries;

/// <summary>A Ledger stream checkpoint and global entry watermark read atomically.</summary>
public sealed record LedgerReadSnapshot
{
    public LedgerReadSnapshot(LedgerCheckpoint? checkpoint, long entryHighWatermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryHighWatermark);
        // A missing selected checkpoint is valid even when another stream owns
        // entries below the global watermark.
        Checkpoint = checkpoint;
        EntryHighWatermark = entryHighWatermark;
    }

    public LedgerCheckpoint? Checkpoint { get; }
    public long EntryHighWatermark { get; }
}
