namespace PaymentSandbox.Ledger.Persistence;

/// <summary>Raised when another consumer moved the durable ledger source cursor.</summary>
public sealed class LedgerCheckpointConflictException(string message)
    : InvalidOperationException(message);
