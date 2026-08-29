using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Ledger.Persistence;

/// <summary>SQLite-backed append-only provisional payment ledger.</summary>
public sealed class SqliteLedgerStore(LedgerDatabase database) : ILedgerStore
{
    private readonly LedgerDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<LedgerCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(chainId, router);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        return await ReadCheckpointAsync(connection, null, chainId, router, cancellationToken);
    }

    public async ValueTask<LedgerCommitResult> CommitAsync(
        LedgerCheckpoint? expectedPrevious,
        CanonicalPaymentBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatch(expectedPrevious, batch);
        var next = new LedgerCheckpoint(
            batch.ChainId,
            batch.Router,
            batch.ThroughTransitionId,
            checked((expectedPrevious?.Revision ?? 0) + 1),
            batch.Fingerprint,
            batch.RecordedAtUtc);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        // The source cursor and every derived entry share one transaction. A
        // crash can therefore expose neither half of a processed source batch.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        LedgerCheckpoint? current = await ReadCheckpointAsync(
            connection,
            transaction,
            batch.ChainId,
            batch.Router,
            cancellationToken);

        if (current != expectedPrevious)
        {
            // A lost response is accepted only when the source cursor, revision,
            // source-fact fingerprint, and every derived entry match exactly.
            if (current is not null && RepresentsSameCommit(current, next))
            {
                await VerifyBatchEntriesAsync(connection, transaction, batch, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new LedgerCommitResult(LedgerCommitDisposition.Replayed, current);
            }

            throw new LedgerCheckpointConflictException(
                "The durable ledger checkpoint changed before this source batch could commit.");
        }

        foreach (CanonicalPaymentChange change in batch.Changes)
        {
            foreach (PaymentRecordedObservation payment in change.Payments)
            {
                await ApplyPaymentChangeAsync(
                    connection,
                    transaction,
                    batch,
                    change.Transition,
                    payment,
                    cancellationToken);
            }
        }

        await WriteCheckpointAsync(
            connection,
            transaction,
            expectedPrevious,
            next,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LedgerCommitResult(LedgerCommitDisposition.Applied, next);
    }

    public async ValueTask<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        EvmChainId chainId,
        EvmAddress router,
        EvmHash blockHash,
        EvmHash transactionHash,
        long logIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(chainId, router);
        ArgumentNullException.ThrowIfNull(blockHash);
        ArgumentNullException.ThrowIfNull(transactionHash);
        ArgumentOutOfRangeException.ThrowIfNegative(logIndex);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, kind, source_transition_id, source_checkpoint_revision,
                   block_number, payment_id, payer_address, token_address,
                   merchant_address, amount_raw, reverses_entry_id,
                   source_changed_at_utc, recorded_at_utc
            FROM canonical_payment_ledger_entries
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND block_hash = $blockHash AND transaction_hash = $transactionHash
              AND log_index = $logIndex
            ORDER BY source_transition_id;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$blockHash", blockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", transactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", logIndex);
        var entries = new List<LedgerEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader, chainId, router, blockHash, transactionHash, logIndex));
        }

        return entries;
    }

    private static void ValidateBatch(
        LedgerCheckpoint? previous,
        CanonicalPaymentBatch batch)
    {
        if (previous is not null &&
            (previous.ChainId != batch.ChainId || previous.Router != batch.Router))
        {
            throw new ArgumentException(
                "The previous ledger checkpoint belongs to a different source stream.",
                nameof(previous));
        }

        long previousTransitionId = previous?.LastSourceTransitionId ?? 0;
        if (batch.ThroughTransitionId <= previousTransitionId)
        {
            throw new ArgumentException(
                "The ledger source target must advance beyond the previous checkpoint.",
                nameof(batch));
        }

        long lastChangeId = previousTransitionId;
        foreach (CanonicalPaymentChange change in batch.Changes)
        {
            if (change.Transition.TransitionId <= lastChangeId ||
                change.Transition.TransitionId > batch.ThroughTransitionId)
            {
                throw new ArgumentException(
                    "Ledger changes must advance in source transition order.",
                    nameof(batch));
            }

            lastChangeId = change.Transition.TransitionId;
        }
    }

    private static bool RepresentsSameCommit(
        LedgerCheckpoint current,
        LedgerCheckpoint expected) =>
        current.ChainId == expected.ChainId &&
        current.Router == expected.Router &&
        current.LastSourceTransitionId == expected.LastSourceTransitionId &&
        current.Revision == expected.Revision &&
        string.Equals(
            current.LastBatchFingerprint,
            expected.LastBatchFingerprint,
            StringComparison.Ordinal);

    private static async Task ApplyPaymentChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalPaymentBatch batch,
        BlockCanonicalityTransition transition,
        PaymentRecordedObservation payment,
        CancellationToken cancellationToken)
    {
        long? activeEntryId = await FindActiveEntryBeforeTransitionAsync(
            connection,
            transaction,
            payment,
            transition.TransitionId,
            cancellationToken);
        LedgerEntryKind kind;
        long? reversesEntryId;
        if (transition.Canonicality == BlockCanonicality.Canonical)
        {
            if (activeEntryId is not null)
            {
                throw new InvalidOperationException(
                    $"Payment occurrence {payment.TransactionHash}/{payment.LogIndex} " +
                    "already has an unreversed canonical effect.");
            }

            kind = LedgerEntryKind.CanonicalPayment;
            reversesEntryId = null;
        }
        else
        {
            if (activeEntryId is null)
            {
                throw new InvalidOperationException(
                    $"Payment occurrence {payment.TransactionHash}/{payment.LogIndex} " +
                    "cannot be reversed without an active canonical effect.");
            }

            kind = LedgerEntryKind.CanonicalPaymentReversal;
            // A reversal points to the active generation instead of mutating or
            // deleting it. Re-canonicalization later creates a new generation.
            reversesEntryId = activeEntryId;
        }

        await InsertOrVerifyEntryAsync(
            connection,
            transaction,
            batch.RecordedAtUtc,
            transition,
            payment,
            kind,
            reversesEntryId,
            cancellationToken);
    }

    private static async Task<long?> FindActiveEntryBeforeTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentRecordedObservation payment,
        long beforeTransitionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT credit.entry_id
            FROM canonical_payment_ledger_entries AS credit
            WHERE credit.chain_id = $chainId
              AND credit.router_address = $routerAddress
              AND credit.block_hash = $blockHash
              AND credit.transaction_hash = $transactionHash
              AND credit.log_index = $logIndex
              AND credit.kind = 'canonical_payment'
              AND credit.source_transition_id < $beforeTransitionId
              AND NOT EXISTS (
                  SELECT 1
                  FROM canonical_payment_ledger_entries AS reversal
                  WHERE reversal.reverses_entry_id = credit.entry_id
                    AND reversal.source_transition_id < $beforeTransitionId
              )
            ORDER BY credit.source_transition_id DESC
            LIMIT 2;
            """;
        AddOccurrenceParameters(command, payment);
        command.Parameters.AddWithValue("$beforeTransitionId", beforeTransitionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        long entryId = reader.GetInt64(0);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "More than one unreversed canonical effect exists for one payment occurrence.");
        }

        return entryId;
    }

    private static async Task InsertOrVerifyEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset recordedAtUtc,
        BlockCanonicalityTransition transition,
        PaymentRecordedObservation payment,
        LedgerEntryKind kind,
        long? reversesEntryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO canonical_payment_ledger_entries (
                chain_id, router_address, kind, source_transition_id,
                source_checkpoint_revision, block_number, block_hash,
                transaction_hash, log_index, payment_id, payer_address,
                token_address, merchant_address, amount_raw, reverses_entry_id,
                source_changed_at_utc, recorded_at_utc)
            VALUES (
                $chainId, $routerAddress, $kind, $sourceTransitionId,
                $sourceCheckpointRevision, $blockNumber, $blockHash,
                $transactionHash, $logIndex, $paymentId, $payerAddress,
                $tokenAddress, $merchantAddress, $amountRaw, $reversesEntryId,
                $sourceChangedAtUtc, $recordedAtUtc)
            ON CONFLICT(
                chain_id, router_address, source_transition_id,
                block_hash, transaction_hash, log_index
            ) DO NOTHING;
            """;
        AddEntryParameters(command, recordedAtUtc, transition, payment, kind, reversesEntryId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await VerifyEntryAsync(
                connection,
                transaction,
                transition,
                payment,
                kind,
                reversesEntryId,
                cancellationToken);
        }
    }

    private static async Task VerifyBatchEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalPaymentBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (CanonicalPaymentChange change in batch.Changes)
        {
            foreach (PaymentRecordedObservation payment in change.Payments)
            {
                long? activeBefore = await FindActiveEntryBeforeTransitionAsync(
                    connection,
                    transaction,
                    payment,
                    change.Transition.TransitionId,
                    cancellationToken);
                LedgerEntryKind kind = change.Transition.Canonicality == BlockCanonicality.Canonical
                    ? LedgerEntryKind.CanonicalPayment
                    : LedgerEntryKind.CanonicalPaymentReversal;
                long? reversesEntryId = kind == LedgerEntryKind.CanonicalPayment
                    ? null
                    : activeBefore ?? throw new InvalidOperationException(
                        "The replayed reversal has no prior active canonical effect.");
                if (kind == LedgerEntryKind.CanonicalPayment && activeBefore is not null)
                {
                    throw new InvalidOperationException(
                        "The replayed canonical effect overlaps an earlier active effect.");
                }

                await VerifyEntryAsync(
                    connection,
                    transaction,
                    change.Transition,
                    payment,
                    kind,
                    reversesEntryId,
                    cancellationToken);
            }
        }
    }

    private static async Task VerifyEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlockCanonicalityTransition transition,
        PaymentRecordedObservation payment,
        LedgerEntryKind kind,
        long? reversesEntryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT kind, source_checkpoint_revision, block_number, payment_id,
                   payer_address, token_address, merchant_address, amount_raw,
                   reverses_entry_id, source_changed_at_utc
            FROM canonical_payment_ledger_entries
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND source_transition_id = $sourceTransitionId
              AND block_hash = $blockHash AND transaction_hash = $transactionHash
              AND log_index = $logIndex;
            """;
        AddStreamParameters(command, payment.ChainId, payment.Router);
        command.Parameters.AddWithValue("$sourceTransitionId", transition.TransitionId);
        command.Parameters.AddWithValue("$blockHash", payment.BlockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", payment.TransactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", payment.LogIndex);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        bool matches = await reader.ReadAsync(cancellationToken) &&
            string.Equals(reader.GetString(0), FormatKind(kind), StringComparison.Ordinal) &&
            reader.GetInt64(1) == transition.CheckpointRevision &&
            reader.GetInt64(2) == payment.BlockNumber &&
            string.Equals(reader.GetString(3), payment.PaymentId.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(4), payment.Payer.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(5), payment.Token.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(6), payment.Merchant.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(7), payment.Amount.ToString(), StringComparison.Ordinal) &&
            ReadNullableInt64(reader, 8) == reversesEntryId &&
            ParseTimestamp(reader.GetString(9)) == transition.ChangedAtUtc;
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Stored ledger entry for transition {transition.TransitionId} and " +
                $"occurrence {payment.TransactionHash}/{payment.LogIndex} does not match the source batch.");
        }
    }

    private static async Task<LedgerCheckpoint?> ReadCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT last_source_transition_id, revision,
                   last_batch_fingerprint, updated_at_utc
            FROM ledger_checkpoints
            WHERE chain_id = $chainId AND router_address = $routerAddress;
            """;
        AddStreamParameters(command, chainId, router);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LedgerCheckpoint(
            chainId,
            router,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)));
    }

    private static async Task WriteCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerCheckpoint? previous,
        LedgerCheckpoint next,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        AddStreamParameters(command, next.ChainId, next.Router);
        command.Parameters.AddWithValue("$lastSourceTransitionId", next.LastSourceTransitionId);
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$lastBatchFingerprint", next.LastBatchFingerprint);
        command.Parameters.AddWithValue("$updatedAtUtc", FormatTimestamp(next.UpdatedAtUtc));
        if (previous is null)
        {
            command.CommandText =
                """
                INSERT INTO ledger_checkpoints (
                    chain_id, router_address, last_source_transition_id,
                    revision, last_batch_fingerprint, updated_at_utc)
                VALUES (
                    $chainId, $routerAddress, $lastSourceTransitionId,
                    $revision, $lastBatchFingerprint, $updatedAtUtc);
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE ledger_checkpoints
                SET last_source_transition_id = $lastSourceTransitionId,
                    revision = $revision,
                    last_batch_fingerprint = $lastBatchFingerprint,
                    updated_at_utc = $updatedAtUtc
                WHERE chain_id = $chainId AND router_address = $routerAddress
                  AND last_source_transition_id = $previousTransitionId
                  AND revision = $previousRevision
                  AND last_batch_fingerprint = $previousFingerprint;
                """;
            command.Parameters.AddWithValue(
                "$previousTransitionId",
                previous.LastSourceTransitionId);
            command.Parameters.AddWithValue("$previousRevision", previous.Revision);
            command.Parameters.AddWithValue("$previousFingerprint", previous.LastBatchFingerprint);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new LedgerCheckpointConflictException(
                "The durable ledger checkpoint changed while this batch was being written.");
        }
    }

    private static LedgerEntry ReadEntry(
        SqliteDataReader reader,
        EvmChainId chainId,
        EvmAddress router,
        EvmHash blockHash,
        EvmHash transactionHash,
        long logIndex) =>
        new(
            reader.GetInt64(0),
            ParseKind(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetInt64(3),
            chainId,
            router,
            reader.GetInt64(4),
            blockHash,
            transactionHash,
            logIndex,
            PaymentId.Parse(reader.GetString(5)),
            EvmAddress.Parse(reader.GetString(6)),
            EvmAddress.Parse(reader.GetString(7)),
            EvmAddress.Parse(reader.GetString(8)),
            new RawTokenAmount(BigInteger.Parse(
                reader.GetString(9),
                NumberStyles.None,
                CultureInfo.InvariantCulture)),
            ReadNullableInt64(reader, 10),
            ParseTimestamp(reader.GetString(11)),
            ParseTimestamp(reader.GetString(12)));

    private static void AddEntryParameters(
        SqliteCommand command,
        DateTimeOffset recordedAtUtc,
        BlockCanonicalityTransition transition,
        PaymentRecordedObservation payment,
        LedgerEntryKind kind,
        long? reversesEntryId)
    {
        AddOccurrenceParameters(command, payment);
        command.Parameters.AddWithValue("$kind", FormatKind(kind));
        command.Parameters.AddWithValue("$sourceTransitionId", transition.TransitionId);
        command.Parameters.AddWithValue(
            "$sourceCheckpointRevision",
            transition.CheckpointRevision);
        command.Parameters.AddWithValue("$blockNumber", payment.BlockNumber);
        command.Parameters.AddWithValue("$paymentId", payment.PaymentId.Value);
        command.Parameters.AddWithValue("$payerAddress", payment.Payer.Value);
        command.Parameters.AddWithValue("$tokenAddress", payment.Token.Value);
        command.Parameters.AddWithValue("$merchantAddress", payment.Merchant.Value);
        command.Parameters.AddWithValue("$amountRaw", payment.Amount.ToString());
        command.Parameters.AddWithValue(
            "$reversesEntryId",
            reversesEntryId is null ? DBNull.Value : reversesEntryId.Value);
        command.Parameters.AddWithValue(
            "$sourceChangedAtUtc",
            FormatTimestamp(transition.ChangedAtUtc));
        command.Parameters.AddWithValue("$recordedAtUtc", FormatTimestamp(recordedAtUtc));
    }

    private static void AddOccurrenceParameters(
        SqliteCommand command,
        PaymentRecordedObservation payment)
    {
        AddStreamParameters(command, payment.ChainId, payment.Router);
        command.Parameters.AddWithValue("$blockHash", payment.BlockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", payment.TransactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", payment.LogIndex);
    }

    private static void AddStreamParameters(
        SqliteCommand command,
        EvmChainId chainId,
        EvmAddress router)
    {
        command.Parameters.AddWithValue("$chainId", chainId.ToString());
        command.Parameters.AddWithValue("$routerAddress", router.Value);
    }

    private static void ValidateStream(EvmChainId? chainId, EvmAddress? router)
    {
        ArgumentNullException.ThrowIfNull(chainId);
        ArgumentNullException.ThrowIfNull(router);
        if (router.IsZero)
        {
            throw new ArgumentException("The Router address cannot be zero.", nameof(router));
        }
    }

    private static string FormatKind(LedgerEntryKind kind) =>
        kind switch
        {
            LedgerEntryKind.CanonicalPayment => "canonical_payment",
            LedgerEntryKind.CanonicalPaymentReversal => "canonical_payment_reversal",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static LedgerEntryKind ParseKind(string value) =>
        value switch
        {
            "canonical_payment" => LedgerEntryKind.CanonicalPayment,
            "canonical_payment_reversal" => LedgerEntryKind.CanonicalPaymentReversal,
            _ => throw new InvalidOperationException($"Stored ledger kind '{value}' is not supported."),
        };

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
