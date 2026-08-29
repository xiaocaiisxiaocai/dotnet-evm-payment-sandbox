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

        IReadOnlyList<ObservedBlock> blocks;
        try
        {
            blocks = await ReadBlocksAsync(
                fromBlockNumber,
                throughBlockNumber,
                previous?.LastBlockHash,
                cancellationToken);
        }
        catch (ChainParentMismatchException exception) when (
            previous is not null && exception.BlockNumber == fromBlockNumber)
        {
            return await RecoverFromReorganizationAsync(
                previous,
                throughBlockNumber,
                cancellationToken);
        }
        IReadOnlyList<RpcPaymentRecordedLog> rawLogs = await ObserveLogsAsync(
            fromBlockNumber,
            throughBlockNumber,
            cancellationToken);
        EnsureLogLimit(rawLogs);

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

    private async Task<ChainObservationResult> RecoverFromReorganizationAsync(
        ChainObservationCheckpoint previous,
        long throughBlockNumber,
        CancellationToken cancellationToken)
    {
        ObservedBlock commonAncestor = await FindCommonAncestorAsync(previous, cancellationToken);
        long replacementStart = checked(commonAncestor.Number + 1);

        // Forward work and detached history have independent limits. Their sum
        // is therefore the largest replacement range recovery may ever read.
        long replacementCount = checked(throughBlockNumber - commonAncestor.Number);
        long maximumRecoveryCount = checked((long)_policy.MaxBatchSize + _policy.MaxReorgDepth);
        if (replacementCount > maximumRecoveryCount)
        {
            throw new ChainObservationException(
                $"The replacement range contains {replacementCount} blocks; " +
                $"the bounded recovery maximum is {maximumRecoveryCount}.");
        }

        IReadOnlyList<ObservedBlock> blocks = await ReadBlocksAsync(
            replacementStart,
            throughBlockNumber,
            commonAncestor.Hash,
            cancellationToken);
        IReadOnlyList<RpcPaymentRecordedLog> rawLogs = await ObserveLogsAsync(
            replacementStart,
            throughBlockNumber,
            cancellationToken);
        EnsureLogLimit(rawLogs);
        IReadOnlyList<PaymentRecordedObservation> payments = DecodeLogs(rawLogs, blocks);
        DateTimeOffset observedAtUtc = _timeProvider.GetUtcNow();
        var replacement = new ChainObservationBatch(
            _policy.ChainId,
            _policy.Router,
            _policy.StartBlockNumber,
            blocks,
            payments,
            observedAtUtc);

        ObservationCommitResult committed = await _store.CommitReorganizationAsync(
            previous,
            commonAncestor,
            replacement,
            cancellationToken);
        return new ChainObservationResult(
            ChainObservationResult.FromStore(committed.Disposition),
            committed.Checkpoint,
            blocks.Count,
            payments.Count,
            DetachedBlockCount: checked((int)(previous.LastBlockNumber - commonAncestor.Number)));
    }

    private async Task<ObservedBlock> FindCommonAncestorAsync(
        ChainObservationCheckpoint previous,
        CancellationToken cancellationToken)
    {
        for (int depth = 0; depth <= _policy.MaxReorgDepth; depth++)
        {
            long blockNumber = checked(previous.LastBlockNumber - depth);
            if (blockNumber < previous.StartBlockNumber)
            {
                break;
            }

            ObservedBlock? stored = await _store.GetCanonicalBlockAsync(
                _policy.ChainId,
                _policy.Router,
                blockNumber,
                cancellationToken);
            if (stored is null)
            {
                throw new ChainObservationException(
                    $"The durable canonical block at height {blockNumber} is missing.");
            }

            ObservedBlock observed = await ReadBlockAsync(blockNumber, cancellationToken);
            if (observed == stored)
            {
                return stored;
            }

            // The configured first block has no earlier durable observation
            // with which this component could prove a common ancestor.
            if (blockNumber == previous.StartBlockNumber)
            {
                break;
            }
        }

        throw new ChainObservationException(
            $"No common ancestor was found within the configured {_policy.MaxReorgDepth}-block reorg limit.");
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
        EvmHash? expectedParent,
        CancellationToken cancellationToken)
    {
        var blocks = new List<ObservedBlock>();

        for (long number = fromBlockNumber; ; number = checked(number + 1))
        {
            ObservedBlock block = await ReadBlockAsync(number, cancellationToken);

            if (expectedParent is not null && block.ParentHash != expectedParent)
            {
                throw new ChainParentMismatchException(
                    number,
                    expectedParent,
                    block.ParentHash);
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

    private async Task<ObservedBlock> ReadBlockAsync(
        long blockNumber,
        CancellationToken cancellationToken)
    {
        RpcBlockHeader? observed;
        try
        {
            observed = await _rpc.GetBlockAsync(blockNumber, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ChainObservationException(
                $"The RPC block observation failed for block {blockNumber}.",
                exception);
        }

        RpcBlockHeader raw = observed
            ?? throw new ChainObservationException($"RPC returned no block {blockNumber}.");
        if (raw.Number != blockNumber)
        {
            throw new ChainObservationException(
                $"RPC returned block number {raw.Number} for requested block {blockNumber}.");
        }

        try
        {
            return new ObservedBlock(
                blockNumber,
                EvmHash.Parse(raw.Hash ?? string.Empty),
                EvmHash.Parse(raw.ParentHash ?? string.Empty));
        }
        catch (FormatException exception)
        {
            throw new ChainObservationException(
                $"RPC returned a malformed hash for block {blockNumber}: {exception.Message}");
        }
    }

    private void EnsureLogLimit(IReadOnlyList<RpcPaymentRecordedLog> rawLogs)
    {
        if (rawLogs.Count > _policy.MaxLogsPerBatch)
        {
            throw new ChainObservationException(
                $"RPC returned {rawLogs.Count} logs; the configured maximum is {_policy.MaxLogsPerBatch}.");
        }
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
