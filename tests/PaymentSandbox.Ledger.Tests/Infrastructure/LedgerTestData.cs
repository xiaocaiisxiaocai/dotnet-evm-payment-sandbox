using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Ledger.Persistence;

namespace PaymentSandbox.Ledger.Tests.Infrastructure;

internal static class LedgerTestData
{
    internal static readonly DateTimeOffset Now =
        new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
    internal static readonly EvmChainId ChainId = new(31_337);
    internal static readonly EvmAddress Router =
        EvmAddress.Parse("0x1111111111111111111111111111111111111111");

    internal static EvmHash Hash(char digit) => EvmHash.Parse($"0x{new string(digit, 64)}");

    internal static PaymentRecordedObservation Payment(
        long blockNumber = 101,
        char blockHash = '2',
        char transactionHash = 'c',
        long logIndex = 3,
        BigInteger? amount = null) =>
        new(
            ChainId,
            Router,
            blockNumber,
            Hash(blockHash),
            Hash(transactionHash),
            logIndex,
            PaymentId.Parse($"0x{new string('a', 64)}"),
            EvmAddress.Parse("0x4444444444444444444444444444444444444444"),
            EvmAddress.Parse("0x2222222222222222222222222222222222222222"),
            EvmAddress.Parse("0x3333333333333333333333333333333333333333"),
            new RawTokenAmount(amount ?? new BigInteger(1_250_000)));

    internal static BlockCanonicalityTransition Transition(
        long transitionId,
        BlockCanonicality canonicality = BlockCanonicality.Canonical,
        long checkpointRevision = 1,
        long blockNumber = 101,
        char blockHash = '2') =>
        new(
            transitionId,
            ChainId,
            Router,
            blockNumber,
            Hash(blockHash),
            checkpointRevision,
            canonicality,
            canonicality == BlockCanonicality.Canonical ? "observed" : "reorg_orphaned",
            Now.AddSeconds(transitionId));

    internal static CanonicalPaymentBatch Batch(
        long throughTransitionId,
        IReadOnlyList<CanonicalPaymentChange> changes,
        DateTimeOffset? recordedAtUtc = null) =>
        new(ChainId, Router, throughTransitionId, changes, recordedAtUtc ?? Now);

    internal static LedgerDatabase CreateDatabase(string path) =>
        new(new LedgerDatabaseOptions(path), new FixedTimeProvider(Now));

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
