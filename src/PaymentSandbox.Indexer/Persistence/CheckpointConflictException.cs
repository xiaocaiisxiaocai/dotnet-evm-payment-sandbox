namespace PaymentSandbox.Indexer.Persistence;

/// <summary>Raised when another scanner advanced or changed the expected stream.</summary>
public sealed class CheckpointConflictException(string message)
    : InvalidOperationException(message);
