using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Ledger.Entries;

/// <summary>A caller-selected source interval prepared for one atomic ledger commit.</summary>
public sealed record CanonicalPaymentBatch
{
    public CanonicalPaymentBatch(
        EvmChainId chainId,
        EvmAddress router,
        long throughTransitionId,
        IReadOnlyList<CanonicalPaymentChange> changes,
        DateTimeOffset recordedAtUtc)
    {
        ChainId = chainId ?? throw new ArgumentNullException(nameof(chainId));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughTransitionId);
        ArgumentNullException.ThrowIfNull(changes);
        CanonicalPaymentChange[] snapshot = changes.ToArray();
        long previousTransitionId = 0;
        foreach (CanonicalPaymentChange change in snapshot)
        {
            BlockCanonicalityTransition transition = change.Transition;
            if (transition.ChainId != chainId || transition.Router != router)
            {
                throw new ArgumentException(
                    "Every canonicality change must belong to the batch stream.",
                    nameof(changes));
            }

            if (transition.TransitionId <= previousTransitionId ||
                transition.TransitionId > throughTransitionId)
            {
                throw new ArgumentException(
                    "Canonicality changes must be strictly ordered within the source target.",
                    nameof(changes));
            }

            previousTransitionId = transition.TransitionId;
        }

        ThroughTransitionId = throughTransitionId;
        Changes = Array.AsReadOnly(snapshot);
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        Fingerprint = ComputeFingerprint(chainId, router, throughTransitionId, snapshot);
    }

    public EvmChainId ChainId { get; }

    public EvmAddress Router { get; }

    public long ThroughTransitionId { get; }

    public IReadOnlyList<CanonicalPaymentChange> Changes { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>SHA-256 over source facts only; local recording time is excluded.</summary>
    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        EvmChainId chainId,
        EvmAddress router,
        long throughTransitionId,
        IReadOnlyList<CanonicalPaymentChange> changes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // A domain/version prefix prevents the same byte sequence from being
        // interpreted as another protocol if this serialization evolves later.
        AppendString(hash, "payment-sandbox/canonical-payment-ledger-batch/v1");
        AppendString(hash, chainId.ToString());
        AppendString(hash, router.Value);
        AppendInt64(hash, throughTransitionId);
        AppendInt64(hash, changes.Count);
        foreach (CanonicalPaymentChange change in changes)
        {
            BlockCanonicalityTransition transition = change.Transition;
            AppendInt64(hash, transition.TransitionId);
            AppendInt64(hash, transition.BlockNumber);
            AppendString(hash, transition.BlockHash.Value);
            AppendInt64(hash, transition.CheckpointRevision);
            AppendInt64(hash, (long)transition.Canonicality);
            AppendString(hash, transition.Reason);
            AppendString(
                hash,
                transition.ChangedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            AppendInt64(hash, change.Payments.Count);
            foreach (PaymentRecordedObservation payment in change.Payments)
            {
                AppendString(hash, payment.TransactionHash.Value);
                AppendInt64(hash, payment.LogIndex);
                AppendString(hash, payment.PaymentId.Value);
                AppendString(hash, payment.Payer.Value);
                AppendString(hash, payment.Token.Value);
                AppendString(hash, payment.Merchant.Value);
                AppendString(hash, payment.Amount.ToString());
            }
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
