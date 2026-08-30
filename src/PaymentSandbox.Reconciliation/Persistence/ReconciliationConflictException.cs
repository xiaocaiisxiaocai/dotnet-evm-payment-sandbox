namespace PaymentSandbox.Reconciliation.Persistence;

public sealed class ReconciliationConflictException(string message) : InvalidOperationException(message);
