using PaymentSandbox.Contracts.Identity;

namespace PaymentSandbox.Contracts.PaymentRouter;

/// <summary>Creates a local Router encoder only after identity verification succeeds.</summary>
public sealed class PaymentRouterConnector
{
    private readonly PaymentRouterIdentityVerifier _identityVerifier;

    public PaymentRouterConnector(IPaymentRouterIdentityRpc rpc)
    {
        _identityVerifier = new PaymentRouterIdentityVerifier(rpc);
    }

    public async Task<VerifiedPaymentRouterClient> ConnectAsync(
        PaymentRouterTrustPolicy policy,
        CancellationToken cancellationToken = default)
    {
        VerifiedPaymentRouterIdentity identity = await _identityVerifier
            .VerifyAsync(policy, cancellationToken)
            .ConfigureAwait(false);

        return new VerifiedPaymentRouterClient(identity);
    }
}
