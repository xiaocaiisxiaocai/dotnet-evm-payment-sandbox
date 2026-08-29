namespace PaymentSandbox.Contracts.PaymentRouter;

/// <summary>Destination and ABI calldata only; this is not a signed transaction.</summary>
/// <remarks>
/// Encoding proves neither user consent nor payment validity. A later application
/// layer must bind intent, sender, fees, nonce, chain, and signing policy before
/// anything may be submitted to a node.
/// </remarks>
public sealed record EncodedPaymentRouterCall(string ContractAddress, string Data);
