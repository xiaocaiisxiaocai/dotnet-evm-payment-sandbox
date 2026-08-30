using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Proof of one consumed login challenge; it is not a session or authorization.</summary>
public sealed record SiweAuthenticationResult(
    EvmAddress Address,
    EvmChainId ChainId,
    DateTimeOffset AuthenticatedAtUtc);
