using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Ledger.Entries;

/// <summary>One source canonicality transition and its exact payment occurrences.</summary>
public sealed record CanonicalPaymentChange
{
    public CanonicalPaymentChange(
        BlockCanonicalityTransition transition,
        IReadOnlyList<PaymentRecordedObservation> payments)
    {
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        ArgumentNullException.ThrowIfNull(payments);
        PaymentRecordedObservation[] snapshot = payments.ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (PaymentRecordedObservation payment in snapshot)
        {
            if (payment.ChainId != transition.ChainId ||
                payment.Router != transition.Router ||
                payment.BlockNumber != transition.BlockNumber ||
                payment.BlockHash != transition.BlockHash)
            {
                throw new ArgumentException(
                    "Every payment must belong to the transition's exact block occurrence.",
                    nameof(payments));
            }

            string identity = $"{payment.TransactionHash}:{payment.LogIndex}";
            if (!identities.Add(identity))
            {
                throw new ArgumentException(
                    "A canonicality change cannot contain a duplicate payment occurrence.",
                    nameof(payments));
            }
        }

        Payments = Array.AsReadOnly(snapshot);
    }

    public BlockCanonicalityTransition Transition { get; }

    public IReadOnlyList<PaymentRecordedObservation> Payments { get; }
}
