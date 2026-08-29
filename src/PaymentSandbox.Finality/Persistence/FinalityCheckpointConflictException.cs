namespace PaymentSandbox.Finality.Persistence;

public sealed class FinalityCheckpointConflictException(string message)
    : InvalidOperationException(message);
