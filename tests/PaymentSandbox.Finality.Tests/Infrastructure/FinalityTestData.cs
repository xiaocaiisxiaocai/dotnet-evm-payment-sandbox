using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Persistence;
using PaymentSandbox.Finality.Policy;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Finality.Tests.Infrastructure;

internal static class FinalityTestData
{
    internal static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    internal static readonly EvmChainId ChainId = new(31_337);
    internal static readonly EvmAddress Router =
        EvmAddress.Parse("0x1111111111111111111111111111111111111111");

    internal static EvmHash Hash(char digit) => EvmHash.Parse($"0x{new string(digit, 64)}");

    internal static ConfirmationFinalityPolicy Policy(
        long requiredConfirmations = 3,
        int maxEntries = 100,
        int maxEffects = 100) =>
        new(ChainId, Router, "local-confirmations-v1", requiredConfirmations, maxEntries, maxEffects);

    internal static LedgerCheckpoint LedgerCheckpoint(
        long lastSourceTransitionId = 4,
        long revision = 1) =>
        new(
            ChainId,
            Router,
            lastSourceTransitionId,
            revision,
            new string('a', 64),
            Now.AddMinutes(revision));

    internal static ChainObservationSnapshot Snapshot(
        long headBlockNumber = 103,
        char headHash = '4',
        long checkpointRevision = 1,
        long highWatermark = 4) =>
        new(
            new ChainObservationCheckpoint(
                ChainId,
                Router,
                100,
                headBlockNumber,
                Hash(headHash),
                checkpointRevision,
                Now.AddMinutes(checkpointRevision)),
            highWatermark);

    internal static LedgerEntry Effect(
        long entryId = 1,
        long blockNumber = 101,
        char blockHash = '2',
        char transactionHash = 'c',
        long logIndex = 3) =>
        new(
            entryId,
            LedgerEntryKind.CanonicalPayment,
            SourceTransitionId: entryId,
            SourceCheckpointRevision: 1,
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
            new RawTokenAmount(new BigInteger(1_250_000)),
            ReversesEntryId: null,
            Now.AddSeconds(entryId),
            Now.AddMinutes(1));

    internal static LedgerEntry Reversal(LedgerEntry effect, long entryId = 2) =>
        effect with
        {
            EntryId = entryId,
            Kind = LedgerEntryKind.CanonicalPaymentReversal,
            SourceTransitionId = entryId,
            SourceCheckpointRevision = 2,
            ReversesEntryId = effect.EntryId,
            SourceChangedAtUtc = Now.AddSeconds(entryId),
            RecordedAtUtc = Now.AddMinutes(2),
        };

    internal static FinalityEvaluationBatch Batch(
        ConfirmationFinalityPolicy policy,
        long throughLedgerEntryId,
        LedgerCheckpoint ledgerCheckpoint,
        ChainObservationSnapshot snapshot,
        IReadOnlyList<LedgerEntry> entries,
        DateTimeOffset? recordedAtUtc = null) =>
        new(
            policy,
            throughLedgerEntryId,
            ledgerCheckpoint,
            snapshot,
            entries,
            recordedAtUtc ?? Now);

    internal static PaymentRecordedObservation Payment(
        long blockNumber = 101,
        char blockHash = '2',
        char transactionHash = 'c',
        long logIndex = 3) =>
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
            new RawTokenAmount(1_250_000));

    internal static FinalityDatabase CreateDatabase(string path) =>
        new(new FinalityDatabaseOptions(path), new FixedTimeProvider(Now));

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
