using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Ledger.Entries;

/// <summary>Durable proof of the source high-watermark processed by one ledger stream.</summary>
public sealed record LedgerCheckpoint
{
    public LedgerCheckpoint(
        EvmChainId chainId,
        EvmAddress router,
        long lastSourceTransitionId,
        long revision,
        string lastBatchFingerprint,
        DateTimeOffset updatedAtUtc)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lastSourceTransitionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentNullException.ThrowIfNull(lastBatchFingerprint);
        if (lastBatchFingerprint.Length != 64 ||
            lastBatchFingerprint.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The ledger batch fingerprint must be 32 lowercase hexadecimal bytes.",
                nameof(lastBatchFingerprint));
        }

        LastSourceTransitionId = lastSourceTransitionId;
        Revision = revision;
        LastBatchFingerprint = lastBatchFingerprint;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long LastSourceTransitionId { get; }

    public long Revision { get; }

    public string LastBatchFingerprint { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
