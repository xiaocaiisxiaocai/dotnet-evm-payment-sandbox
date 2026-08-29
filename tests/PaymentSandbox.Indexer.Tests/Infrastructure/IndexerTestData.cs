using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Indexer.Rpc;

namespace PaymentSandbox.Indexer.Tests.Infrastructure;

internal static class IndexerTestData
{
    internal static readonly DateTimeOffset Now =
        new(2026, 8, 30, 8, 30, 0, TimeSpan.Zero);
    internal static readonly EvmChainId ChainId = new(31_337);
    internal static readonly EvmAddress Router =
        EvmAddress.Parse("0x1111111111111111111111111111111111111111");

    internal static EvmHash Hash(char digit) => EvmHash.Parse($"0x{new string(digit, 64)}");

    internal static RpcBlockHeader RpcBlock(long number, char hash, char parent) =>
        new(number, Hash(hash).Value, Hash(parent).Value);

    internal static RpcPaymentRecordedLog RpcPayment(
        long blockNumber = 101,
        char blockHash = '2',
        bool removed = false,
        string? contractAddress = null,
        BigInteger? amount = null) =>
        new(
            contractAddress ?? Router.Value,
            blockNumber,
            Hash(blockHash).Value,
            Hash('c').Value,
            3,
            removed,
            PaymentId.Parse($"0x{new string('a', 64)}").ToBytes(),
            "0x4444444444444444444444444444444444444444",
            "0x2222222222222222222222222222222222222222",
            "0x3333333333333333333333333333333333333333",
            amount ?? new BigInteger(1_250_000));

    internal static ChainObservationBatch Batch() =>
        new(
            ChainId,
            Router,
            100,
            [
                new ObservedBlock(100, Hash('1'), Hash('0')),
                new ObservedBlock(101, Hash('2'), Hash('1')),
            ],
            [
                new PaymentRecordedObservation(
                    ChainId,
                    Router,
                    101,
                    Hash('2'),
                    Hash('c'),
                    3,
                    PaymentId.Parse($"0x{new string('a', 64)}"),
                    EvmAddress.Parse("0x4444444444444444444444444444444444444444"),
                    EvmAddress.Parse("0x2222222222222222222222222222222222222222"),
                    EvmAddress.Parse("0x3333333333333333333333333333333333333333"),
                    new RawTokenAmount(1_250_000)),
            ],
            Now);

    internal static IndexerDatabase CreateDatabase(string path) =>
        new(new IndexerDatabaseOptions(path), new FixedTimeProvider(Now));

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
