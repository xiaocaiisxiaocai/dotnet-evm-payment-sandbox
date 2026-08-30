using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Permits.Erc2612;

/// <summary>Unsigned Router call plus the EOA that must submit it.</summary>
public sealed record PreparedErc2612Payment(
    PaymentId PaymentId,
    EvmAddress RequiredSender,
    EvmAddress Token,
    EvmAddress Merchant,
    RawTokenAmount Amount,
    string PermitDigest,
    EncodedPaymentRouterCall Call)
{
    public override string ToString() =>
        $"Prepared ERC-2612 payment {PaymentId.Value} from {RequiredSender.Value} " +
        $"to {Merchant.Value} for {Amount} raw units (calldata redacted)";
}
