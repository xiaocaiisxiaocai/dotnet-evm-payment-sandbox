using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Finality.Persistence;

/// <summary>Durable proof of the exact source snapshots used by one evaluation.</summary>
public sealed record FinalityCheckpoint
{
    public FinalityCheckpoint(
        EvmChainId chainId,
        EvmAddress router,
        long lastLedgerEntryId,
        long ledgerCheckpointRevision,
        long lastIndexerTransitionId,
        long headBlockNumber,
        EvmHash headBlockHash,
        long headCheckpointRevision,
        long revision,
        string policyId,
        long requiredConfirmationCount,
        string policyFingerprint,
        string lastBatchFingerprint,
        DateTimeOffset updatedAtUtc)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(lastLedgerEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledgerCheckpointRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lastIndexerTransitionId);
        ArgumentOutOfRangeException.ThrowIfNegative(headBlockNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headCheckpointRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredConfirmationCount);
        ValidateFingerprint(policyFingerprint, nameof(policyFingerprint));
        ValidateFingerprint(lastBatchFingerprint, nameof(lastBatchFingerprint));
        LastLedgerEntryId = lastLedgerEntryId;
        LedgerCheckpointRevision = ledgerCheckpointRevision;
        LastIndexerTransitionId = lastIndexerTransitionId;
        HeadBlockNumber = headBlockNumber;
        HeadBlockHash = headBlockHash ?? throw new ArgumentNullException(nameof(headBlockHash));
        HeadCheckpointRevision = headCheckpointRevision;
        Revision = revision;
        PolicyId = policyId;
        RequiredConfirmationCount = requiredConfirmationCount;
        PolicyFingerprint = policyFingerprint;
        LastBatchFingerprint = lastBatchFingerprint;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public EvmChainId ChainId { get; }
    public EvmAddress Router { get; }
    public long LastLedgerEntryId { get; }
    public long LedgerCheckpointRevision { get; }
    public long LastIndexerTransitionId { get; }
    public long HeadBlockNumber { get; }
    public EvmHash HeadBlockHash { get; }
    public long HeadCheckpointRevision { get; }
    public long Revision { get; }
    public string PolicyId { get; }
    public long RequiredConfirmationCount { get; }
    public string PolicyFingerprint { get; }
    public string LastBatchFingerprint { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    private static void ValidateFingerprint(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A fingerprint must be 32 lowercase hexadecimal bytes.",
                parameterName);
        }
    }
}
