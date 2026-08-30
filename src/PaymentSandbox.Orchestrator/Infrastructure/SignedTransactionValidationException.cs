namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>Signals that opaque signer output did not reproduce the requested transaction.</summary>
public sealed class SignedTransactionValidationException(string message) : Exception(message);
