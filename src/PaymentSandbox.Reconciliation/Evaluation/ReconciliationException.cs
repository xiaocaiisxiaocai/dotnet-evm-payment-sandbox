namespace PaymentSandbox.Reconciliation.Evaluation;

public sealed class ReconciliationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
