using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Abstractions;

/// <summary>Signs only the complete typed transaction supplied by the orchestrator.</summary>
/// <remarks>
/// Implementations are test-only. They must not log key material or signed raw
/// bytes, and a future real adapter must verify the signed payload round-trips
/// to the exact unsigned fields before returning it.
/// </remarks>
public interface ITestTransactionSigner
{
    Task<SignedTransactionPayload> SignAsync(
        UnsignedPaymentTransaction transaction,
        CancellationToken cancellationToken = default);
}
