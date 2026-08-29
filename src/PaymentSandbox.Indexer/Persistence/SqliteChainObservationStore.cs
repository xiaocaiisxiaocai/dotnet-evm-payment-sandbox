using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
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

        await WriteCheckpointAsync(
            connection,
            transaction,
            expectedPrevious,
            next,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ObservationCommitResult(ObservationCommitDisposition.Applied, next);
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
