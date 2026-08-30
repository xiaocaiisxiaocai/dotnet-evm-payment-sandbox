using System.Numerics;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Permits.Preflight;

/// <summary>One exact-block read-only view of the facts needed for ERC-2612.</summary>
/// <remarks>
/// An implementation must read code and calls at <em>one</em> block number and
/// reject a reorg that changes that numbered block while the reads are running.
/// The interface deliberately has no account, signer, send, or receipt method.
/// </remarks>
public interface IErc2612TokenSnapshotRpc
{
    Task<Erc2612TokenSnapshotObservation> ObserveAsync(
        EvmAddress token,
        EvmAddress owner,
        CancellationToken cancellationToken = default);
}

public sealed record Erc2612TokenSnapshotObservation(
    BigInteger ChainId,
    EvmAddress Token,
    EvmAddress Owner,
    long BlockNumber,
    string BlockHash,
    string RuntimeCode,
    string TokenName,
    string DomainSeparator,
    BigInteger Nonce);
