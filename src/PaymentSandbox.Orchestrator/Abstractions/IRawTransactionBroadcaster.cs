using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Abstractions;

public enum TransactionBroadcastOutcomeKind
{
    Accepted,
    AlreadyKnown,
    Unknown,
    Rejected,
}

/// <summary>A bounded, non-sensitive classification of one broadcast call.</summary>
public sealed record TransactionBroadcastOutcome
{
    public TransactionBroadcastOutcome(TransactionBroadcastOutcomeKind kind, string code)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 64 || code.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "A broadcast code must contain 1-64 lowercase ASCII letters, digits, '.', '_' or '-'.",
                nameof(code));
        }

        Kind = kind;
        Code = code;
    }

    public TransactionBroadcastOutcomeKind Kind { get; }
    public string Code { get; }
}

/// <summary>Broadcasts only an already-persisted signed payload.</summary>
public interface IRawTransactionBroadcaster
{
    Task<TransactionBroadcastOutcome> BroadcastAsync(
        EvmChainId chainId,
        SignedTransactionPayload payload,
        CancellationToken cancellationToken = default);
}
