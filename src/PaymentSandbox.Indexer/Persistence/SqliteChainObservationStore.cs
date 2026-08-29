using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;

namespace PaymentSandbox.Indexer.Persistence;

/// <summary>SQLite storage for append-only observations and one active checkpoint.</summary>
public sealed class SqliteChainObservationStore(IndexerDatabase database)
    : IChainObservationStore
{
    private readonly IndexerDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<ChainObservationCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chainId);
        ArgumentNullException.ThrowIfNull(router);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        return await ReadCheckpointAsync(connection, null, chainId, router, cancellationToken);
    }

    public async ValueTask<long> GetCanonicalityHighWatermarkAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(transition_id), 0) FROM block_canonicality_transitions;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async ValueTask<IReadOnlyList<BlockCanonicalityTransition>>
        GetCanonicalityTransitionsAsync(
            EvmChainId chainId,
            EvmAddress router,
            long afterTransitionId,
            long throughTransitionId,
            int maxCount,
            CancellationToken cancellationToken = default)
    {
        ValidateReadStream(chainId, router);
        ArgumentOutOfRangeException.ThrowIfNegative(afterTransitionId);
        if (throughTransitionId < afterTransitionId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughTransitionId),
                "The transition target cannot precede the exclusive cursor.");
        }

        ValidateReadLimit(maxCount);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT transition_id, block_number, block_hash,
                   checkpoint_revision, canonicality, reason, changed_at_utc
            FROM block_canonicality_transitions
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND transition_id > $afterTransitionId
              AND transition_id <= $throughTransitionId
            ORDER BY transition_id
            LIMIT $maxCount;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$afterTransitionId", afterTransitionId);
        command.Parameters.AddWithValue("$throughTransitionId", throughTransitionId);
        command.Parameters.AddWithValue("$maxCount", maxCount);
        var transitions = new List<BlockCanonicalityTransition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transitions.Add(new BlockCanonicalityTransition(
                reader.GetInt64(0),
                chainId,
                router,
                reader.GetInt64(1),
                EvmHash.Parse(reader.GetString(2)),
                reader.GetInt64(3),
                ParseCanonicality(reader.GetString(4)),
                reader.GetString(5),
                ParseTimestamp(reader.GetString(6))));
        }

        return transitions;
    }

    public async ValueTask<IReadOnlyList<PaymentRecordedObservation>> GetPaymentsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        EvmHash blockHash,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateReadStream(chainId, router);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentNullException.ThrowIfNull(blockHash);
        ValidateReadLimit(maxCount);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT transaction_hash, log_index, payment_id, payer_address,
                   token_address, merchant_address, amount_raw
            FROM payment_recorded_observations
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND block_number = $blockNumber AND block_hash = $blockHash
            ORDER BY transaction_hash, log_index
            LIMIT $maxCount;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$blockNumber", blockNumber);
        command.Parameters.AddWithValue("$blockHash", blockHash.Value);
        command.Parameters.AddWithValue("$maxCount", maxCount);
        var payments = new List<PaymentRecordedObservation>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new PaymentRecordedObservation(
                chainId,
                router,
                blockNumber,
                blockHash,
                EvmHash.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                PaymentId.Parse(reader.GetString(2)),
                EvmAddress.Parse(reader.GetString(3)),
                EvmAddress.Parse(reader.GetString(4)),
                EvmAddress.Parse(reader.GetString(5)),
                new RawTokenAmount(BigInteger.Parse(
                    reader.GetString(6),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture))));
        }

        return payments;
    }

    public async ValueTask<ObservedBlock?> GetCanonicalBlockAsync(
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chainId);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        return await ReadCanonicalBlockAsync(
            connection,
            null,
            chainId,
            router,
            blockNumber,
            cancellationToken);
    }

    public async ValueTask<ObservationCommitResult> CommitBatchAsync(
        ChainObservationCheckpoint? expectedPrevious,
        ChainObservationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatch(expectedPrevious, batch);

        var next = new ChainObservationCheckpoint(
            batch.ChainId,
            batch.Router,
            batch.StartBlockNumber,
            batch.LastBlock.Number,
            batch.LastBlock.Hash,
            checked((expectedPrevious?.Revision ?? 0) + 1),
            batch.ObservedAtUtc);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        ChainObservationCheckpoint? current = await ReadCheckpointAsync(
            connection,
            transaction,
            batch.ChainId,
            batch.Router,
            cancellationToken);

        if (current != expectedPrevious)
        {
            // A caller may lose the response after SQLite committed. Treat the
            // exact resulting cursor as a retry only after every row is checked.
            if (current is not null && RepresentsSamePosition(current, next))
            {
                await VerifyBatchRowsAsync(connection, transaction, batch, cancellationToken);
                foreach (ObservedBlock block in batch.Blocks)
                {
                    await VerifyCanonicalityTransitionAsync(
                        connection,
                        transaction,
                        batch.ChainId,
                        batch.Router,
                        block,
                        next.Revision,
                        "canonical",
                        "observed",
                        cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return new ObservationCommitResult(ObservationCommitDisposition.Replayed, current);
            }

            throw new CheckpointConflictException(
                "The durable checkpoint changed before this observation batch could commit.");
        }

        for (int index = 0; index < batch.Blocks.Count; index++)
        {
            ObservedBlock block = batch.Blocks[index];
            await InsertOrVerifyBlockAsync(connection, transaction, batch, block, cancellationToken);
        }

        foreach (PaymentRecordedObservation payment in batch.Payments)
        {
            await InsertOrVerifyPaymentAsync(
                connection,
                transaction,
                batch.ObservedAtUtc,
                payment,
                cancellationToken);
        }


        foreach (ObservedBlock block in batch.Blocks)
        {
            await InsertOrVerifyCanonicalityTransitionAsync(
                connection,
                transaction,
                batch.ChainId,
                batch.Router,
                block,
                next.Revision,
                "canonical",
                "observed",
                batch.ObservedAtUtc,
                cancellationToken);
        }

        await WriteCheckpointAsync(
            connection,
            transaction,
            expectedPrevious,
            next,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ObservationCommitResult(ObservationCommitDisposition.Applied, next);
    }

    public async ValueTask<ObservationCommitResult> CommitReorganizationAsync(
        ChainObservationCheckpoint expectedPrevious,
        ObservedBlock commonAncestor,
        ChainObservationBatch replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPrevious);
        ArgumentNullException.ThrowIfNull(commonAncestor);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReorganization(expectedPrevious, commonAncestor, replacement);

        var next = new ChainObservationCheckpoint(
            replacement.ChainId,
            replacement.Router,
            replacement.StartBlockNumber,
            replacement.LastBlock.Number,
            replacement.LastBlock.Hash,
            checked(expectedPrevious.Revision + 1),
            replacement.ObservedAtUtc);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        ChainObservationCheckpoint? current = await ReadCheckpointAsync(
            connection,
            transaction,
            replacement.ChainId,
            replacement.Router,
            cancellationToken);

        if (current != expectedPrevious)
        {
            if (current is not null && RepresentsSamePosition(current, next))
            {
                await VerifyCanonicalAncestorAsync(
                    connection,
                    transaction,
                    replacement.ChainId,
                    replacement.Router,
                    commonAncestor,
                    cancellationToken);
                await VerifyBatchRowsAsync(connection, transaction, replacement, cancellationToken);
                await VerifyReorganizationTransitionsAsync(
                    connection,
                    transaction,
                    expectedPrevious,
                    commonAncestor,
                    replacement,
                    next.Revision,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ObservationCommitResult(ObservationCommitDisposition.Replayed, current);
            }

            throw new CheckpointConflictException(
                "The durable checkpoint changed before this reorganization could commit.");
        }

        await VerifyCanonicalAncestorAsync(
            connection,
            transaction,
            replacement.ChainId,
            replacement.Router,
            commonAncestor,
            cancellationToken);

        IReadOnlyList<ObservedBlock> detached = await ReadCanonicalRangeAsync(
            connection,
            transaction,
            replacement.ChainId,
            replacement.Router,
            checked(commonAncestor.Number + 1),
            expectedPrevious.LastBlockNumber,
            cancellationToken);
        ValidateDetachedSuffix(expectedPrevious, commonAncestor, detached);
        if (detached[0] == replacement.Blocks[0])
        {
            throw new ArgumentException(
                "The selected ancestor is not the highest common block.",
                nameof(commonAncestor));
        }

        foreach (ObservedBlock block in replacement.Blocks)
        {
            await InsertOrVerifyBlockAsync(connection, transaction, replacement, block, cancellationToken);
        }

        foreach (PaymentRecordedObservation payment in replacement.Payments)
        {
            await InsertOrVerifyPaymentAsync(
                connection,
                transaction,
                replacement.ObservedAtUtc,
                payment,
                cancellationToken);
        }

        foreach (ObservedBlock block in detached)
        {
            await InsertOrVerifyCanonicalityTransitionAsync(
                connection,
                transaction,
                replacement.ChainId,
                replacement.Router,
                block,
                next.Revision,
                "noncanonical",
                "reorg_detached",
                replacement.ObservedAtUtc,
                cancellationToken);
        }

        foreach (ObservedBlock block in replacement.Blocks)
        {
            await InsertOrVerifyCanonicalityTransitionAsync(
                connection,
                transaction,
                replacement.ChainId,
                replacement.Router,
                block,
                next.Revision,
                "canonical",
                "reorg_replacement",
                replacement.ObservedAtUtc,
                cancellationToken);
        }

        await WriteCheckpointAsync(
            connection,
            transaction,
            expectedPrevious,
            next,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ObservationCommitResult(ObservationCommitDisposition.Reorganized, next);
    }

    private static void ValidateBatch(
        ChainObservationCheckpoint? previous,
        ChainObservationBatch batch)
    {
        if (batch.Router.IsZero)
        {
            throw new ArgumentException("The observation Router cannot be zero.", nameof(batch));
        }

        long expectedFirstNumber = previous is null
            ? batch.StartBlockNumber
            : checked(previous.LastBlockNumber + 1);
        if (previous is not null &&
            (previous.ChainId != batch.ChainId ||
             previous.Router != batch.Router ||
             previous.StartBlockNumber != batch.StartBlockNumber))
        {
            throw new ArgumentException(
                "The previous checkpoint belongs to a different observation stream.",
                nameof(previous));
        }

        EvmHash? expectedParent = previous?.LastBlockHash;
        long expectedNumber = expectedFirstNumber;
        var blockIdentities = new Dictionary<long, EvmHash>();
        for (int index = 0; index < batch.Blocks.Count; index++)
        {
            ObservedBlock block = batch.Blocks[index];
            if (block.Number != expectedNumber)
            {
                throw new ArgumentException(
                    "Observation blocks must be complete and consecutive.",
                    nameof(batch));
            }

            if (expectedParent is not null && block.ParentHash != expectedParent)
            {
                throw new ArgumentException(
                    "Observation blocks do not form one parent-linked chain.",
                    nameof(batch));
            }

            blockIdentities.Add(block.Number, block.Hash);
            expectedParent = block.Hash;
            if (index < batch.Blocks.Count - 1)
            {
                expectedNumber = checked(expectedNumber + 1);
            }
        }

        var paymentIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (PaymentRecordedObservation payment in batch.Payments)
        {
            if (payment.ChainId != batch.ChainId ||
                payment.Router != batch.Router ||
                !blockIdentities.TryGetValue(payment.BlockNumber, out EvmHash? blockHash) ||
                payment.BlockHash != blockHash)
            {
                throw new ArgumentException(
                    "Every payment observation must belong to a block in this batch.",
                    nameof(batch));
            }

            string identity = $"{payment.BlockHash}:{payment.TransactionHash}:{payment.LogIndex}";
            if (!paymentIdentities.Add(identity))
            {
                throw new ArgumentException(
                    "The batch contains a duplicate event occurrence.",
                    nameof(batch));
            }
        }
    }

    private static void ValidateReorganization(
        ChainObservationCheckpoint previous,
        ObservedBlock commonAncestor,
        ChainObservationBatch replacement)
    {
        if (previous.ChainId != replacement.ChainId ||
            previous.Router != replacement.Router ||
            previous.StartBlockNumber != replacement.StartBlockNumber)
        {
            throw new ArgumentException(
                "The previous checkpoint belongs to a different observation stream.",
                nameof(previous));
        }

        if (commonAncestor.Number < previous.StartBlockNumber ||
            commonAncestor.Number >= previous.LastBlockNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commonAncestor),
                "The common ancestor must be inside the stored range and precede its tip.");
        }

        var ancestorCheckpoint = new ChainObservationCheckpoint(
            previous.ChainId,
            previous.Router,
            previous.StartBlockNumber,
            commonAncestor.Number,
            commonAncestor.Hash,
            previous.Revision,
            previous.UpdatedAtUtc);
        ValidateBatch(ancestorCheckpoint, replacement);
    }

    private static void ValidateDetachedSuffix(
        ChainObservationCheckpoint previous,
        ObservedBlock commonAncestor,
        IReadOnlyList<ObservedBlock> detached)
    {
        long expectedCount = checked(previous.LastBlockNumber - commonAncestor.Number);
        if (detached.Count != expectedCount)
        {
            throw new InvalidOperationException(
                "The durable canonical suffix is incomplete; the reorganization was not applied.");
        }

        EvmHash expectedParent = commonAncestor.Hash;
        for (int index = 0; index < detached.Count; index++)
        {
            ObservedBlock block = detached[index];
            long expectedNumber = checked(commonAncestor.Number + index + 1L);
            if (block.Number != expectedNumber || block.ParentHash != expectedParent)
            {
                throw new InvalidOperationException(
                    "The durable canonical suffix is not one complete parent-linked chain.");
            }

            expectedParent = block.Hash;
        }

        if (detached[^1].Hash != previous.LastBlockHash)
        {
            throw new InvalidOperationException(
                "The durable canonical suffix does not terminate at the checkpoint hash.");
        }
    }

    private static bool RepresentsSamePosition(
        ChainObservationCheckpoint current,
        ChainObservationCheckpoint expected) =>
        current.ChainId == expected.ChainId &&
        current.Router == expected.Router &&
        current.StartBlockNumber == expected.StartBlockNumber &&
        current.LastBlockNumber == expected.LastBlockNumber &&
        current.LastBlockHash == expected.LastBlockHash &&
        current.Revision == expected.Revision;

    private static async Task<ChainObservationCheckpoint?> ReadCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT start_block_number, last_block_number, last_block_hash,
                   revision, updated_at_utc
            FROM indexer_checkpoints
            WHERE chain_id = $chainId AND router_address = $routerAddress;
            """;
        command.Parameters.AddWithValue("$chainId", chainId.ToString());
        command.Parameters.AddWithValue("$routerAddress", router.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ChainObservationCheckpoint(
            chainId,
            router,
            reader.GetInt64(0),
            reader.GetInt64(1),
            EvmHash.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)));
    }

    private static async Task<ObservedBlock?> ReadCanonicalBlockAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvmChainId chainId,
        EvmAddress router,
        long blockNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT b.block_hash, b.parent_hash
            FROM observed_blocks AS b
            JOIN block_canonicality_transitions AS t
              ON t.chain_id = b.chain_id
             AND t.router_address = b.router_address
             AND t.block_number = b.block_number
             AND t.block_hash = b.block_hash
            WHERE b.chain_id = $chainId
              AND b.router_address = $routerAddress
              AND b.block_number = $blockNumber
              AND t.transition_id = (
                  SELECT MAX(t2.transition_id)
                  FROM block_canonicality_transitions AS t2
                  WHERE t2.chain_id = t.chain_id
                    AND t2.router_address = t.router_address
                    AND t2.block_number = t.block_number
                    AND t2.block_hash = t.block_hash
              )
              AND t.canonicality = 'canonical';
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$blockNumber", blockNumber);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = new ObservedBlock(
            blockNumber,
            EvmHash.Parse(reader.GetString(0)),
            EvmHash.Parse(reader.GetString(1)));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"More than one block is currently canonical at height {blockNumber}.");
        }

        return result;
    }

    private static async Task<IReadOnlyList<ObservedBlock>> ReadCanonicalRangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvmChainId chainId,
        EvmAddress router,
        long fromBlockNumber,
        long throughBlockNumber,
        CancellationToken cancellationToken)
    {
        var blocks = new List<ObservedBlock>();
        for (long number = fromBlockNumber; ; number = checked(number + 1))
        {
            ObservedBlock? block = await ReadCanonicalBlockAsync(
                connection,
                transaction,
                chainId,
                router,
                number,
                cancellationToken);
            if (block is not null)
            {
                blocks.Add(block);
            }

            if (number == throughBlockNumber)
            {
                break;
            }
        }

        return blocks;
    }

    private static async Task VerifyCanonicalAncestorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvmChainId chainId,
        EvmAddress router,
        ObservedBlock commonAncestor,
        CancellationToken cancellationToken)
    {
        ObservedBlock? durableAncestor = await ReadCanonicalBlockAsync(
            connection,
            transaction,
            chainId,
            router,
            commonAncestor.Number,
            cancellationToken);
        if (durableAncestor != commonAncestor)
        {
            throw new CheckpointConflictException(
                "The selected common ancestor is no longer canonical.");
        }
    }

    private static async Task InsertOrVerifyBlockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChainObservationBatch batch,
        ObservedBlock block,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO observed_blocks (
                chain_id, router_address, block_number, block_hash,
                parent_hash, observed_at_utc)
            VALUES (
                $chainId, $routerAddress, $blockNumber, $blockHash,
                $parentHash, $observedAtUtc)
            ON CONFLICT(chain_id, router_address, block_number, block_hash) DO NOTHING;
            """;
        AddStreamParameters(command, batch.ChainId, batch.Router);
        command.Parameters.AddWithValue("$blockNumber", block.Number);
        command.Parameters.AddWithValue("$blockHash", block.Hash.Value);
        command.Parameters.AddWithValue("$parentHash", block.ParentHash.Value);
        command.Parameters.AddWithValue("$observedAtUtc", FormatTimestamp(batch.ObservedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await VerifyBlockRowAsync(connection, transaction, batch, block, cancellationToken);
        }
    }

    private static async Task InsertOrVerifyPaymentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedAtUtc,
        PaymentRecordedObservation payment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO payment_recorded_observations (
                chain_id, router_address, block_number, block_hash,
                transaction_hash, log_index, payment_id, payer_address,
                token_address, merchant_address, amount_raw, observed_at_utc)
            VALUES (
                $chainId, $routerAddress, $blockNumber, $blockHash,
                $transactionHash, $logIndex, $paymentId, $payerAddress,
                $tokenAddress, $merchantAddress, $amountRaw, $observedAtUtc)
            ON CONFLICT(
                chain_id, router_address, block_hash, transaction_hash, log_index
            ) DO NOTHING;
            """;
        AddStreamParameters(command, payment.ChainId, payment.Router);
        AddPaymentParameters(command, payment);
        command.Parameters.AddWithValue("$observedAtUtc", FormatTimestamp(observedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await VerifyPaymentRowAsync(connection, transaction, payment, cancellationToken);
        }
    }

    private static async Task InsertOrVerifyCanonicalityTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvmChainId chainId,
        EvmAddress router,
        ObservedBlock block,
        long checkpointRevision,
        string canonicality,
        string reason,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO block_canonicality_transitions (
                chain_id, router_address, block_number, block_hash,
                checkpoint_revision, canonicality, reason, changed_at_utc)
            VALUES (
                $chainId, $routerAddress, $blockNumber, $blockHash,
                $checkpointRevision, $canonicality, $reason, $changedAtUtc)
            ON CONFLICT(
                chain_id, router_address, block_number, block_hash,
                checkpoint_revision, canonicality
            ) DO NOTHING;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$blockNumber", block.Number);
        command.Parameters.AddWithValue("$blockHash", block.Hash.Value);
        command.Parameters.AddWithValue("$checkpointRevision", checkpointRevision);
        command.Parameters.AddWithValue("$canonicality", canonicality);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$changedAtUtc", FormatTimestamp(changedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await VerifyCanonicalityTransitionAsync(
                connection,
                transaction,
                chainId,
                router,
                block,
                checkpointRevision,
                canonicality,
                reason,
                cancellationToken);
        }
    }

    private static async Task VerifyCanonicalityTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvmChainId chainId,
        EvmAddress router,
        ObservedBlock block,
        long checkpointRevision,
        string canonicality,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT reason
            FROM block_canonicality_transitions
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND block_number = $blockNumber AND block_hash = $blockHash
              AND checkpoint_revision = $checkpointRevision
              AND canonicality = $canonicality;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$blockNumber", block.Number);
        command.Parameters.AddWithValue("$blockHash", block.Hash.Value);
        command.Parameters.AddWithValue("$checkpointRevision", checkpointRevision);
        command.Parameters.AddWithValue("$canonicality", canonicality);
        string? storedReason = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(storedReason, reason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stored canonicality transition for block {block.Number}/{block.Hash} " +
                "does not match the replayed change.");
        }
    }

    private static async Task WriteCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChainObservationCheckpoint? previous,
        ChainObservationCheckpoint next,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddStreamParameters(command, next.ChainId, next.Router);
        command.Parameters.AddWithValue("$startBlockNumber", next.StartBlockNumber);
        command.Parameters.AddWithValue("$lastBlockNumber", next.LastBlockNumber);
        command.Parameters.AddWithValue("$lastBlockHash", next.LastBlockHash.Value);
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$updatedAtUtc", FormatTimestamp(next.UpdatedAtUtc));

        if (previous is null)
        {
            command.CommandText =
                """
                INSERT INTO indexer_checkpoints (
                    chain_id, router_address, start_block_number,
                    last_block_number, last_block_hash, revision, updated_at_utc)
                VALUES (
                    $chainId, $routerAddress, $startBlockNumber,
                    $lastBlockNumber, $lastBlockHash, $revision, $updatedAtUtc);
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE indexer_checkpoints
                SET last_block_number = $lastBlockNumber,
                    last_block_hash = $lastBlockHash,
                    revision = $revision,
                    updated_at_utc = $updatedAtUtc
                WHERE chain_id = $chainId
                  AND router_address = $routerAddress
                  AND start_block_number = $startBlockNumber
                  AND revision = $previousRevision
                  AND last_block_number = $previousBlockNumber
                  AND last_block_hash = $previousBlockHash;
                """;
            command.Parameters.AddWithValue("$previousRevision", previous.Revision);
            command.Parameters.AddWithValue("$previousBlockNumber", previous.LastBlockNumber);
            command.Parameters.AddWithValue("$previousBlockHash", previous.LastBlockHash.Value);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new CheckpointConflictException(
                "The durable checkpoint changed while this observation batch was being written.");
        }
    }

    private static async Task VerifyBatchRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChainObservationBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (ObservedBlock block in batch.Blocks)
        {
            await VerifyBlockRowAsync(connection, transaction, batch, block, cancellationToken);
        }

        foreach (PaymentRecordedObservation payment in batch.Payments)
        {
            await VerifyPaymentRowAsync(connection, transaction, payment, cancellationToken);
        }
    }

    private static async Task VerifyReorganizationTransitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChainObservationCheckpoint previous,
        ObservedBlock commonAncestor,
        ChainObservationBatch replacement,
        long checkpointRevision,
        CancellationToken cancellationToken)
    {
        foreach (ObservedBlock block in replacement.Blocks)
        {
            await VerifyCanonicalityTransitionAsync(
                connection,
                transaction,
                replacement.ChainId,
                replacement.Router,
                block,
                checkpointRevision,
                "canonical",
                "reorg_replacement",
                cancellationToken);
        }

        IReadOnlyList<ObservedBlock> detached = await ReadTransitionBlocksAsync(
            connection,
            transaction,
            replacement.ChainId,
            replacement.Router,
            checkpointRevision,
            "noncanonical",
            "reorg_detached",
            cancellationToken);
        try
        {
            ValidateDetachedSuffix(previous, commonAncestor, detached);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Stored detach transitions do not match the replayed reorganization.",
                exception);
        }
    }

    private static async Task<IReadOnlyList<ObservedBlock>> ReadTransitionBlocksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvmChainId chainId,
        EvmAddress router,
        long checkpointRevision,
        string canonicality,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT b.block_number, b.block_hash, b.parent_hash
            FROM block_canonicality_transitions AS t
            JOIN observed_blocks AS b
              ON b.chain_id = t.chain_id
             AND b.router_address = t.router_address
             AND b.block_number = t.block_number
             AND b.block_hash = t.block_hash
            WHERE t.chain_id = $chainId AND t.router_address = $routerAddress
              AND t.checkpoint_revision = $checkpointRevision
              AND t.canonicality = $canonicality AND t.reason = $reason
            ORDER BY b.block_number;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$checkpointRevision", checkpointRevision);
        command.Parameters.AddWithValue("$canonicality", canonicality);
        command.Parameters.AddWithValue("$reason", reason);
        var blocks = new List<ObservedBlock>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            blocks.Add(new ObservedBlock(
                reader.GetInt64(0),
                EvmHash.Parse(reader.GetString(1)),
                EvmHash.Parse(reader.GetString(2))));
        }

        return blocks;
    }

    private static async Task VerifyBlockRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChainObservationBatch batch,
        ObservedBlock block,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT parent_hash
            FROM observed_blocks
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND block_number = $blockNumber AND block_hash = $blockHash;
            """;
        AddStreamParameters(command, batch.ChainId, batch.Router);
        command.Parameters.AddWithValue("$blockNumber", block.Number);
        command.Parameters.AddWithValue("$blockHash", block.Hash.Value);
        string? parentHash = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(parentHash, block.ParentHash.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stored block {block.Number}/{block.Hash} does not match the replayed observation.");
        }
    }

    private static async Task VerifyPaymentRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentRecordedObservation payment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT block_number, payment_id, payer_address, token_address,
                   merchant_address, amount_raw
            FROM payment_recorded_observations
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND block_hash = $blockHash AND transaction_hash = $transactionHash
              AND log_index = $logIndex;
            """;
        AddStreamParameters(command, payment.ChainId, payment.Router);
        command.Parameters.AddWithValue("$blockHash", payment.BlockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", payment.TransactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", payment.LogIndex);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        bool matches = await reader.ReadAsync(cancellationToken) &&
            reader.GetInt64(0) == payment.BlockNumber &&
            string.Equals(reader.GetString(1), payment.PaymentId.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), payment.Payer.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(3), payment.Token.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(4), payment.Merchant.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(5), payment.Amount.ToString(), StringComparison.Ordinal);
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Stored event {payment.TransactionHash}/{payment.LogIndex} does not match the replayed observation.");
        }
    }

    private static void AddStreamParameters(
        SqliteCommand command,
        EvmChainId chainId,
        EvmAddress router)
    {
        command.Parameters.AddWithValue("$chainId", chainId.ToString());
        command.Parameters.AddWithValue("$routerAddress", router.Value);
    }

    private static void ValidateReadStream(EvmChainId? chainId, EvmAddress? router)
    {
        ArgumentNullException.ThrowIfNull(chainId);
        ArgumentNullException.ThrowIfNull(router);
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }
    }

    private static void ValidateReadLimit(int maxCount)
    {
        if (maxCount is < 1 or > 100_001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCount),
                "A source read must request between 1 and 100,001 rows.");
        }
    }

    private static BlockCanonicality ParseCanonicality(string value) =>
        value switch
        {
            "canonical" => BlockCanonicality.Canonical,
            "noncanonical" => BlockCanonicality.Noncanonical,
            _ => throw new InvalidOperationException(
                $"Stored canonicality value '{value}' is not supported."),
        };

    private static void AddPaymentParameters(
        SqliteCommand command,
        PaymentRecordedObservation payment)
    {
        command.Parameters.AddWithValue("$blockNumber", payment.BlockNumber);
        command.Parameters.AddWithValue("$blockHash", payment.BlockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", payment.TransactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", payment.LogIndex);
        command.Parameters.AddWithValue("$paymentId", payment.PaymentId.Value);
        command.Parameters.AddWithValue("$payerAddress", payment.Payer.Value);
        command.Parameters.AddWithValue("$tokenAddress", payment.Token.Value);
        command.Parameters.AddWithValue("$merchantAddress", payment.Merchant.Value);
        command.Parameters.AddWithValue("$amountRaw", payment.Amount.ToString());
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
