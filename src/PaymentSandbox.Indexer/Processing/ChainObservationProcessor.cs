using System.Numerics;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Indexer.Persistence;
using PaymentSandbox.Indexer.Rpc;

namespace PaymentSandbox.Indexer.Processing;

/// <summary>Validates and atomically persists one explicit block range.</summary>
public sealed class ChainObservationProcessor
{
    private readonly ChainObservationPolicy _policy;
    private readonly IChainObservationRpc _rpc;
    private readonly IChainObservationStore _store;
    private readonly TimeProvider _timeProvider;

    public ChainObservationProcessor(
        ChainObservationPolicy policy,
        IChainObservationRpc rpc,
        IChainObservationStore store,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Scans from the durable cursor through an exact caller-selected block.</summary>
    public async Task<ChainObservationResult> ScanThroughAsync(
        long throughBlockNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughBlockNumber);
        cancellationToken.ThrowIfCancellationRequested();

        ChainObservationCheckpoint? previous = await _store.GetCheckpointAsync(
            _policy.ChainId,
            _policy.Router,
            cancellationToken);
        ValidateStoredCheckpoint(previous);

        long fromBlockNumber = previous is null
            ? _policy.StartBlockNumber
            : checked(previous.LastBlockNumber + 1);
        if (throughBlockNumber < fromBlockNumber)
        {
            return new ChainObservationResult(
                ChainObservationDisposition.NoWork,
                previous,
                ObservedBlockCount: 0,
                ObservedPaymentCount: 0);
        }

        long blockCount = checked(throughBlockNumber - fromBlockNumber + 1);
        if (blockCount > _policy.MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughBlockNumber),
                $"The requested range contains {blockCount} blocks; the configured maximum is {_policy.MaxBatchSize}.");
        }

        BigInteger observedChainId = await ObserveChainIdAsync(cancellationToken);
        if (observedChainId != _policy.ChainId.Value)
        {
            throw new ChainObservationException(
                $"RPC reported chain ID {observedChainId}, expected {_policy.ChainId}.");
        }

        IReadOnlyList<ObservedBlock> blocks = await ReadBlocksAsync(
            fromBlockNumber,
            throughBlockNumber,
            previous,
            cancellationToken);
        IReadOnlyList<RpcPaymentRecordedLog> rawLogs = await ObserveLogsAsync(
            fromBlockNumber,
            throughBlockNumber,
            cancellationToken);
        if (rawLogs.Count > _policy.MaxLogsPerBatch)
        {
            throw new ChainObservationException(
                $"RPC returned {rawLogs.Count} logs; the configured maximum is {_policy.MaxLogsPerBatch}.");
        }

        IReadOnlyList<PaymentRecordedObservation> payments = DecodeLogs(rawLogs, blocks);
        DateTimeOffset observedAtUtc = _timeProvider.GetUtcNow();
        var batch = new ChainObservationBatch(
            _policy.ChainId,
            _policy.Router,
            _policy.StartBlockNumber,
            blocks,
            payments,
            observedAtUtc);

        ObservationCommitResult committed = await _store.CommitBatchAsync(
            previous,
            batch,
            cancellationToken);
        return new ChainObservationResult(
            ChainObservationResult.FromStore(committed.Disposition),
            committed.Checkpoint,
            blocks.Count,
            payments.Count);
    }

    private void ValidateStoredCheckpoint(ChainObservationCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return;
        }

        if (checkpoint.ChainId != _policy.ChainId ||
            checkpoint.Router != _policy.Router ||
            checkpoint.StartBlockNumber != _policy.StartBlockNumber)
        {
            throw new InvalidOperationException(
                "The stored checkpoint does not match the configured observation stream.");
        }
    }

    private async Task<BigInteger> ObserveChainIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _rpc.GetChainIdAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ChainObservationException("The RPC chain ID observation failed.", exception);
        }
    }

    private async Task<IReadOnlyList<RpcPaymentRecordedLog>> ObserveLogsAsync(
        long fromBlockNumber,
        long throughBlockNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _rpc.GetPaymentRecordedLogsAsync(
                _policy.Router,
                fromBlockNumber,
                throughBlockNumber,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ChainObservationException(
                $"The RPC log observation failed for blocks {fromBlockNumber} through {throughBlockNumber}.",
                exception);
        }
    }

    private async Task<IReadOnlyList<ObservedBlock>> ReadBlocksAsync(
        long fromBlockNumber,
        long throughBlockNumber,
        ChainObservationCheckpoint? previous,
        CancellationToken cancellationToken)
    {
        var blocks = new List<ObservedBlock>();
        EvmHash? expectedParent = previous?.LastBlockHash;

        for (long number = fromBlockNumber; ; number = checked(number + 1))
        {
            RpcBlockHeader? observed;
            try
            {
                observed = await _rpc.GetBlockAsync(number, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ChainObservationException(
                    $"The RPC block observation failed for block {number}.",
                    exception);
            }

            RpcBlockHeader raw = observed
                ?? throw new ChainObservationException($"RPC returned no block {number}.");
            if (raw.Number != number)
            {
                throw new ChainObservationException(
                    $"RPC returned block number {raw.Number} for requested block {number}.");
            }

            ObservedBlock block;
            try
            {
                block = new ObservedBlock(
                    number,
                    EvmHash.Parse(raw.Hash ?? string.Empty),
                    EvmHash.Parse(raw.ParentHash ?? string.Empty));
            }
            catch (FormatException exception)
            {
                throw new ChainObservationException(
                    $"RPC returned a malformed hash for block {number}: {exception.Message}");
            }

            if (expectedParent is not null && block.ParentHash != expectedParent)
            {
                throw new ChainObservationException(
                    $"Block {number} parent {block.ParentHash} does not extend {expectedParent}.");
            }

            blocks.Add(block);
            expectedParent = block.Hash;
            if (number == throughBlockNumber)
            {
                break;
            }
        }

        return blocks;
    }

    private IReadOnlyList<PaymentRecordedObservation> DecodeLogs(
        IReadOnlyList<RpcPaymentRecordedLog> rawLogs,
        IReadOnlyList<ObservedBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(rawLogs);
        var blocksByNumber = blocks.ToDictionary(block => block.Number);
        var payments = new List<PaymentRecordedObservation>(rawLogs.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (RpcPaymentRecordedLog raw in rawLogs)
        {
            if (raw.Removed)
            {
                throw new ChainObservationException(
                    "RPC returned a removed log for an explicit historical block range.");
            }

            if (!TryToLong(raw.BlockNumber, out long blockNumber) ||
                !blocksByNumber.TryGetValue(blockNumber, out ObservedBlock? block))
            {
                throw new ChainObservationException(
                    $"RPC returned a log outside the requested block range: {raw.BlockNumber}.");
            }

            if (!TryToLong(raw.LogIndex, out long logIndex))
            {
                throw new ChainObservationException($"RPC returned invalid log index {raw.LogIndex}.");
            }

            PaymentRecordedObservation payment;
            try
            {
                EvmAddress emitter = EvmAddress.Parse(raw.ContractAddress ?? string.Empty);
                EvmHash blockHash = EvmHash.Parse(raw.BlockHash ?? string.Empty);
                if (emitter != _policy.Router || blockHash != block.Hash)
                {
                    throw new ChainObservationException(
                        "A decoded log does not belong to the configured Router and observed block.");
                }

                payment = new PaymentRecordedObservation(
                    _policy.ChainId,
                    _policy.Router,
                    blockNumber,
                    blockHash,
                    EvmHash.Parse(raw.TransactionHash ?? string.Empty),
                    logIndex,
                    PaymentId.FromBytes(raw.PaymentId ?? []),
                    EvmAddress.Parse(raw.Payer ?? string.Empty),
                    EvmAddress.Parse(raw.Token ?? string.Empty),
                    EvmAddress.Parse(raw.Merchant ?? string.Empty),
                    new RawTokenAmount(raw.Amount));
            }
            catch (ChainObservationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is FormatException or ArgumentException)
            {
                throw new ChainObservationException(
                    $"RPC returned a malformed PaymentRecorded log: {exception.Message}");
            }

            string identity = $"{payment.BlockHash}:{payment.TransactionHash}:{payment.LogIndex}";
            if (!identities.Add(identity))
            {
                throw new ChainObservationException(
                    $"RPC returned duplicate log occurrence {identity}.");
            }

            payments.Add(payment);
        }

        return payments;
    }

    private static bool TryToLong(BigInteger value, out long result)
    {
        if (value < 0 || value > long.MaxValue)
        {
            result = default;
            return false;
        }

        result = (long)value;
        return true;
    }
}
