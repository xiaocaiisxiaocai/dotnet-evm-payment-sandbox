using System.Numerics;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Permits.Preflight;

/// <summary>Policy-matched token facts observed at one exact block.</summary>
public sealed record VerifiedErc2612TokenSnapshot(
    EvmAddress Owner,
    BigInteger Nonce,
    long BlockNumber,
    string BlockHash,
    string RuntimeCodeHash,
    string DomainSeparator)
{
    public override string ToString() =>
        $"Verified ERC-2612 token snapshot for {Owner.Value} at block {BlockNumber} " +
        $"with nonce {Nonce}";
}
