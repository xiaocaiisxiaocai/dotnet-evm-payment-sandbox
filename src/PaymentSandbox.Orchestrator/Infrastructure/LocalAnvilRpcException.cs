namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>A non-sensitive local Anvil identity or response-shape failure.</summary>
public sealed class LocalAnvilRpcException(string message) : Exception(message);
