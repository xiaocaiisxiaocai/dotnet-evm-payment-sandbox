using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Finality.Evaluation;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Indexer.Chain;
using PaymentSandbox.Ledger.Entries;

namespace PaymentSandbox.Finality.Persistence;

/// <summary>SQLite-backed append-only confirmation-policy projection.</summary>
public sealed class SqliteFinalityStore(FinalityDatabase database) : IFinalityStore
{
    private readonly FinalityDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<FinalityCheckpoint?> GetCheckpointAsync(
        EvmChainId chainId,
        EvmAddress router,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(chainId, router);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        return await ReadCheckpointAsync(connection, null, chainId, router, cancellationToken);
    }

    public async ValueTask<FinalityCommitResult> CommitAsync(
        FinalityCheckpoint? expectedPrevious,
        FinalityEvaluationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatch(expectedPrevious, batch);
        ChainObservationCheckpoint head = batch.ObservationSnapshot.Checkpoint!;
        long nextRevision = checked((expectedPrevious?.Revision ?? 0) + 1);
        var next = new FinalityCheckpoint(
            batch.Policy.ChainId,
            batch.Policy.Router,
            batch.ThroughLedgerEntryId,
            batch.LedgerCheckpoint.Revision,
            batch.ObservationSnapshot.CanonicalityHighWatermark,
            head.LastBlockNumber,
            head.LastBlockHash,
            head.Revision,
            nextRevision,
            batch.Policy.PolicyId,
            batch.Policy.RequiredConfirmations,
            batch.Policy.Fingerprint,
            batch.Fingerprint,
            batch.RecordedAtUtc);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        // Copied source rows, derived decisions, and the evaluation cursor are
        // indivisible. A crash cannot expose a qualification without the exact
        // source snapshot that justified it.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        FinalityCheckpoint? current = await ReadCheckpointAsync(
            connection,
            transaction,
            batch.Policy.ChainId,
            batch.Policy.Router,
            cancellationToken);
        if (current != expectedPrevious)
        {
            if (current is not null && RepresentsSameCommit(current, next))
            {
                // The first writer may have committed while its caller lost the
                // result. Cursor equality alone is insufficient: re-read every
                // copied source row and deterministically rebuild every decision
                // before reporting that this request was already applied.
                await VerifySourceEntriesAsync(connection, transaction, batch, cancellationToken);
                IReadOnlyList<FinalityDecision> replayDecisions = await BuildDecisionsAsync(
                    connection,
                    transaction,
                    batch,
                    beforeRevision: current.Revision,
                    decisionRevision: current.Revision,
                    cancellationToken);
                await VerifyDecisionsAsync(
                    connection,
                    transaction,
                    batch,
                    replayDecisions,
                    current.Revision,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToResult(FinalityCommitDisposition.Replayed, current, replayDecisions);
            }

            throw new FinalityCheckpointConflictException(
                "The durable finality checkpoint changed before this evaluation could commit.");
        }

        foreach (LedgerEntry entry in batch.NewLedgerEntries)
        {
            await InsertOrVerifySourceEntryAsync(
                connection,
                transaction,
                entry,
                cancellationToken);
        }

        IReadOnlyList<FinalityDecision> decisions = await BuildDecisionsAsync(
            connection,
            transaction,
            batch,
            beforeRevision: nextRevision,
            decisionRevision: nextRevision,
            cancellationToken);
        foreach (FinalityDecision decision in decisions)
        {
            await InsertDecisionAsync(
                connection,
                transaction,
                batch,
                decision,
                cancellationToken);
        }

        await WriteCheckpointAsync(
            connection,
            transaction,
            expectedPrevious,
            next,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(FinalityCommitDisposition.Applied, next, decisions);
    }

    public async ValueTask<IReadOnlyList<FinalityTransition>> GetTransitionsAsync(
        EvmChainId chainId,
        EvmAddress router,
        long ledgerEffectEntryId,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(chainId, router);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledgerEffectEntryId);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT transition_id, finality_revision, kind, revokes_transition_id,
                   head_block_number, head_block_hash, head_checkpoint_revision,
                   confirmation_count, required_confirmation_count, reason,
                   recorded_at_utc
            FROM payment_finality_transitions
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND ledger_effect_entry_id = $ledgerEffectEntryId
            ORDER BY transition_id;
            """;
        AddStreamParameters(command, chainId, router);
        command.Parameters.AddWithValue("$ledgerEffectEntryId", ledgerEffectEntryId);
        var transitions = new List<FinalityTransition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transitions.Add(new FinalityTransition(
                reader.GetInt64(0),
                reader.GetInt64(1),
                ParseKind(reader.GetString(2)),
                ledgerEffectEntryId,
                ReadNullableInt64(reader, 3),
                chainId,
                router,
                reader.GetInt64(4),
                EvmHash.Parse(reader.GetString(5)),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                ParseReason(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10))));
        }

        return transitions;
    }

    private static void ValidateBatch(
        FinalityCheckpoint? previous,
        FinalityEvaluationBatch batch)
    {
        if (previous is null)
        {
            return;
        }

        if (previous.ChainId != batch.Policy.ChainId || previous.Router != batch.Policy.Router)
        {
            throw new ArgumentException(
                "The previous finality checkpoint belongs to another stream.",
                nameof(previous));
        }

        if (!string.Equals(
                previous.PolicyFingerprint,
                batch.Policy.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A finality policy cannot change inside an existing projection stream.");
        }

        if (batch.ThroughLedgerEntryId < previous.LastLedgerEntryId)
        {
            throw new ArgumentException(
                "The Ledger source cursor cannot move backward.",
                nameof(batch));
        }

        long previousEntryId = previous.LastLedgerEntryId;
        foreach (LedgerEntry entry in batch.NewLedgerEntries)
        {
            if (entry.EntryId <= previousEntryId)
            {
                throw new ArgumentException(
                    "New Ledger entries must advance beyond the finality checkpoint.",
                    nameof(batch));
            }

            previousEntryId = entry.EntryId;
        }
    }

    private static bool RepresentsSameCommit(
        FinalityCheckpoint current,
        FinalityCheckpoint expected) =>
        current.ChainId == expected.ChainId &&
        current.Router == expected.Router &&
        current.LastLedgerEntryId == expected.LastLedgerEntryId &&
        current.LedgerCheckpointRevision == expected.LedgerCheckpointRevision &&
        current.LastIndexerTransitionId == expected.LastIndexerTransitionId &&
        current.HeadBlockNumber == expected.HeadBlockNumber &&
        current.HeadBlockHash == expected.HeadBlockHash &&
        current.HeadCheckpointRevision == expected.HeadCheckpointRevision &&
        current.Revision == expected.Revision &&
        string.Equals(current.PolicyId, expected.PolicyId, StringComparison.Ordinal) &&
        current.RequiredConfirmationCount == expected.RequiredConfirmationCount &&
        string.Equals(current.PolicyFingerprint, expected.PolicyFingerprint, StringComparison.Ordinal) &&
        string.Equals(current.LastBatchFingerprint, expected.LastBatchFingerprint, StringComparison.Ordinal);

    private static async Task<IReadOnlyList<FinalityDecision>> BuildDecisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinalityEvaluationBatch batch,
        long beforeRevision,
        long decisionRevision,
        CancellationToken cancellationToken)
    {
        // Source rows and finality transitions are append-only. For each Ledger
        // effect generation, derive "active" from absence of a reversal and
        // derive "qualified" from its latest earlier finality transition. The
        // comparison below then emits only the state change justified by this
        // evaluation's exact head and policy.
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT effect.ledger_entry_id, effect.block_number,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM finality_source_ledger_entries AS reversal
                       WHERE reversal.reverses_ledger_entry_id = effect.ledger_entry_id
                   ) THEN 0 ELSE 1 END AS is_active,
                   latest.transition_id, latest.kind
            FROM finality_source_ledger_entries AS effect
            LEFT JOIN payment_finality_transitions AS latest
              ON latest.transition_id = (
                  SELECT candidate.transition_id
                  FROM payment_finality_transitions AS candidate
                  WHERE candidate.chain_id = effect.chain_id
                    AND candidate.router_address = effect.router_address
                    AND candidate.ledger_effect_entry_id = effect.ledger_entry_id
                    AND candidate.finality_revision < $beforeRevision
                  ORDER BY candidate.transition_id DESC
                  LIMIT 1
              )
            WHERE effect.chain_id = $chainId
              AND effect.router_address = $routerAddress
              AND effect.kind = 'canonical_payment'
            ORDER BY effect.ledger_entry_id
            LIMIT $maxEffects;
            """;
        AddStreamParameters(command, batch.Policy.ChainId, batch.Policy.Router);
        command.Parameters.AddWithValue("$beforeRevision", beforeRevision);
        command.Parameters.AddWithValue("$maxEffects", checked(batch.Policy.MaxEffectsPerEvaluation + 1));
        var decisions = new List<FinalityDecision>();
        int effectCount = 0;
        ChainObservationCheckpoint head = batch.ObservationSnapshot.Checkpoint!;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            effectCount++;
            if (effectCount > batch.Policy.MaxEffectsPerEvaluation)
            {
                throw new InvalidOperationException(
                    $"The projection contains more than {batch.Policy.MaxEffectsPerEvaluation} payment effects.");
            }

            long effectEntryId = reader.GetInt64(0);
            long blockNumber = reader.GetInt64(1);
            bool isActive = reader.GetInt64(2) == 1;
            long? latestTransitionId = ReadNullableInt64(reader, 3);
            bool isQualified = !reader.IsDBNull(4) &&
                ParseKind(reader.GetString(4)) == FinalityTransitionKind.ConfirmationQualified;
            long confirmations = CountConfirmations(head.LastBlockNumber, blockNumber);
            bool shouldBeQualified = isActive &&
                confirmations >= batch.Policy.RequiredConfirmations;
            if (shouldBeQualified == isQualified)
            {
                continue;
            }

            if (shouldBeQualified)
            {
                decisions.Add(new FinalityDecision(
                    decisionRevision,
                    FinalityTransitionKind.ConfirmationQualified,
                    effectEntryId,
                    RevokesTransitionId: null,
                    confirmations,
                    FinalityTransitionReason.ConfirmationThresholdReached));
            }
            else
            {
                decisions.Add(new FinalityDecision(
                    decisionRevision,
                    FinalityTransitionKind.ConfirmationRevoked,
                    effectEntryId,
                    latestTransitionId ?? throw new InvalidOperationException(
                        "A qualified effect has no transition to revoke."),
                    confirmations,
                    isActive
                        ? FinalityTransitionReason.ConfirmationThresholdLost
                        : FinalityTransitionReason.LedgerEffectReversed));
            }
        }

        return decisions;
    }

    private static long CountConfirmations(long headBlockNumber, long effectBlockNumber)
    {
        if (headBlockNumber < effectBlockNumber)
        {
            return 0;
        }

        long difference = headBlockNumber - effectBlockNumber;
        return difference == long.MaxValue ? long.MaxValue : difference + 1;
    }

    private static async Task InsertOrVerifySourceEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerEntry entry,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO finality_source_ledger_entries (
                ledger_entry_id, chain_id, router_address, kind,
                source_transition_id, source_checkpoint_revision,
                block_number, block_hash, transaction_hash, log_index,
                payment_id, payer_address, token_address, merchant_address,
                amount_raw, reverses_ledger_entry_id, source_changed_at_utc,
                ledger_recorded_at_utc)
            VALUES (
                $ledgerEntryId, $chainId, $routerAddress, $kind,
                $sourceTransitionId, $sourceCheckpointRevision,
                $blockNumber, $blockHash, $transactionHash, $logIndex,
                $paymentId, $payerAddress, $tokenAddress, $merchantAddress,
                $amountRaw, $reversesLedgerEntryId, $sourceChangedAtUtc,
                $ledgerRecordedAtUtc)
            ON CONFLICT(ledger_entry_id) DO NOTHING;
            """;
        AddSourceEntryParameters(command, entry);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await VerifySourceEntryAsync(connection, transaction, entry, cancellationToken);
        }
    }

    private static async Task VerifySourceEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinalityEvaluationBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (LedgerEntry entry in batch.NewLedgerEntries)
        {
            await VerifySourceEntryAsync(connection, transaction, entry, cancellationToken);
        }
    }

    private static async Task VerifySourceEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerEntry entry,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT chain_id, router_address, kind, source_transition_id,
                   source_checkpoint_revision, block_number, block_hash,
                   transaction_hash, log_index, payment_id, payer_address,
                   token_address, merchant_address, amount_raw,
                   reverses_ledger_entry_id, source_changed_at_utc,
                   ledger_recorded_at_utc
            FROM finality_source_ledger_entries
            WHERE ledger_entry_id = $ledgerEntryId;
            """;
        command.Parameters.AddWithValue("$ledgerEntryId", entry.EntryId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        bool matches = await reader.ReadAsync(cancellationToken) &&
            reader.GetString(0) == entry.ChainId.ToString() &&
            reader.GetString(1) == entry.Router.Value &&
            reader.GetString(2) == FormatLedgerKind(entry.Kind) &&
            reader.GetInt64(3) == entry.SourceTransitionId &&
            reader.GetInt64(4) == entry.SourceCheckpointRevision &&
            reader.GetInt64(5) == entry.BlockNumber &&
            reader.GetString(6) == entry.BlockHash.Value &&
            reader.GetString(7) == entry.TransactionHash.Value &&
            reader.GetInt64(8) == entry.LogIndex &&
            reader.GetString(9) == entry.PaymentId.Value &&
            reader.GetString(10) == entry.Payer.Value &&
            reader.GetString(11) == entry.Token.Value &&
            reader.GetString(12) == entry.Merchant.Value &&
            reader.GetString(13) == entry.Amount.ToString() &&
            ReadNullableInt64(reader, 14) == entry.ReversesEntryId &&
            ParseTimestamp(reader.GetString(15)) == entry.SourceChangedAtUtc &&
            ParseTimestamp(reader.GetString(16)) == entry.RecordedAtUtc;
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Stored Finality source entry {entry.EntryId} does not match the Ledger fact.");
        }
    }

    private static async Task InsertDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinalityEvaluationBatch batch,
        FinalityDecision decision,
        CancellationToken cancellationToken)
    {
        ChainObservationCheckpoint head = batch.ObservationSnapshot.Checkpoint!;
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO payment_finality_transitions (
                chain_id, router_address, finality_revision, kind,
                ledger_effect_entry_id, revokes_transition_id,
                head_block_number, head_block_hash, head_checkpoint_revision,
                confirmation_count, required_confirmation_count, reason,
                recorded_at_utc)
            VALUES (
                $chainId, $routerAddress, $finalityRevision, $kind,
                $ledgerEffectEntryId, $revokesTransitionId,
                $headBlockNumber, $headBlockHash, $headCheckpointRevision,
                $confirmationCount, $requiredConfirmationCount, $reason,
                $recordedAtUtc);
            """;
        AddStreamParameters(command, batch.Policy.ChainId, batch.Policy.Router);
        AddDecisionParameters(command, batch, decision, head);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyDecisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinalityEvaluationBatch batch,
        IReadOnlyList<FinalityDecision> decisions,
        long finalityRevision,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT kind, ledger_effect_entry_id, revokes_transition_id,
                   head_block_number, head_block_hash, head_checkpoint_revision,
                   confirmation_count, required_confirmation_count, reason
            FROM payment_finality_transitions
            WHERE chain_id = $chainId AND router_address = $routerAddress
              AND finality_revision = $finalityRevision
            ORDER BY ledger_effect_entry_id;
            """;
        AddStreamParameters(command, batch.Policy.ChainId, batch.Policy.Router);
        command.Parameters.AddWithValue("$finalityRevision", finalityRevision);
        ChainObservationCheckpoint head = batch.ObservationSnapshot.Checkpoint!;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        foreach (FinalityDecision decision in decisions.OrderBy(item => item.LedgerEffectEntryId))
        {
            bool matches = await reader.ReadAsync(cancellationToken) &&
                reader.GetString(0) == FormatKind(decision.Kind) &&
                reader.GetInt64(1) == decision.LedgerEffectEntryId &&
                ReadNullableInt64(reader, 2) == decision.RevokesTransitionId &&
                reader.GetInt64(3) == head.LastBlockNumber &&
                reader.GetString(4) == head.LastBlockHash.Value &&
                reader.GetInt64(5) == head.Revision &&
                reader.GetInt64(6) == decision.ConfirmationCount &&
                reader.GetInt64(7) == batch.Policy.RequiredConfirmations &&
                reader.GetString(8) == FormatReason(decision.Reason);
            if (!matches)
            {
                throw new InvalidOperationException(
                    $"Stored finality decision for Ledger effect {decision.LedgerEffectEntryId} does not match replay.");
            }
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The replayed finality revision contains unexpected extra decisions.");
        }
    }

    private static async Task<FinalityCheckpoint?> ReadCheckpointAsync(
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
            SELECT last_ledger_entry_id, ledger_checkpoint_revision,
                   last_indexer_transition_id, head_block_number,
                   head_block_hash, head_checkpoint_revision, revision,
                   policy_id, required_confirmation_count, policy_fingerprint,
                   last_batch_fingerprint, updated_at_utc
            FROM finality_checkpoints
            WHERE chain_id = $chainId AND router_address = $routerAddress;
            """;
        AddStreamParameters(command, chainId, router);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FinalityCheckpoint(
            chainId,
            router,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            EvmHash.Parse(reader.GetString(4)),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            ParseTimestamp(reader.GetString(11)));
    }

    private static async Task WriteCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinalityCheckpoint? previous,
        FinalityCheckpoint next,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        AddStreamParameters(command, next.ChainId, next.Router);
        AddCheckpointParameters(command, next);
        if (previous is null)
        {
            command.CommandText =
                """
                INSERT INTO finality_checkpoints (
                    chain_id, router_address, last_ledger_entry_id,
                    ledger_checkpoint_revision, last_indexer_transition_id,
                    head_block_number, head_block_hash, head_checkpoint_revision,
                    revision, policy_id, required_confirmation_count,
                    policy_fingerprint, last_batch_fingerprint,
                    updated_at_utc)
                VALUES (
                    $chainId, $routerAddress, $lastLedgerEntryId,
                    $ledgerCheckpointRevision, $lastIndexerTransitionId,
                    $headBlockNumber, $headBlockHash, $headCheckpointRevision,
                    $revision, $policyId, $requiredConfirmationCount,
                    $policyFingerprint, $lastBatchFingerprint,
                    $updatedAtUtc);
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE finality_checkpoints
                SET last_ledger_entry_id = $lastLedgerEntryId,
                    ledger_checkpoint_revision = $ledgerCheckpointRevision,
                    last_indexer_transition_id = $lastIndexerTransitionId,
                    head_block_number = $headBlockNumber,
                    head_block_hash = $headBlockHash,
                    head_checkpoint_revision = $headCheckpointRevision,
                    revision = $revision,
                    policy_id = $policyId,
                    required_confirmation_count = $requiredConfirmationCount,
                    policy_fingerprint = $policyFingerprint,
                    last_batch_fingerprint = $lastBatchFingerprint,
                    updated_at_utc = $updatedAtUtc
                WHERE chain_id = $chainId AND router_address = $routerAddress
                  AND last_ledger_entry_id = $previousLedgerEntryId
                  AND revision = $previousRevision
                  AND last_batch_fingerprint = $previousBatchFingerprint;
                """;
            command.Parameters.AddWithValue("$previousLedgerEntryId", previous.LastLedgerEntryId);
            command.Parameters.AddWithValue("$previousRevision", previous.Revision);
            command.Parameters.AddWithValue(
                "$previousBatchFingerprint",
                previous.LastBatchFingerprint);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new FinalityCheckpointConflictException(
                "The finality checkpoint changed while the evaluation was being written.");
        }
    }

    private static void AddCheckpointParameters(SqliteCommand command, FinalityCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$lastLedgerEntryId", checkpoint.LastLedgerEntryId);
        command.Parameters.AddWithValue(
            "$ledgerCheckpointRevision",
            checkpoint.LedgerCheckpointRevision);
        command.Parameters.AddWithValue(
            "$lastIndexerTransitionId",
            checkpoint.LastIndexerTransitionId);
        command.Parameters.AddWithValue("$headBlockNumber", checkpoint.HeadBlockNumber);
        command.Parameters.AddWithValue("$headBlockHash", checkpoint.HeadBlockHash.Value);
        command.Parameters.AddWithValue(
            "$headCheckpointRevision",
            checkpoint.HeadCheckpointRevision);
        command.Parameters.AddWithValue("$revision", checkpoint.Revision);
        command.Parameters.AddWithValue("$policyId", checkpoint.PolicyId);
        command.Parameters.AddWithValue(
            "$requiredConfirmationCount",
            checkpoint.RequiredConfirmationCount);
        command.Parameters.AddWithValue("$policyFingerprint", checkpoint.PolicyFingerprint);
        command.Parameters.AddWithValue("$lastBatchFingerprint", checkpoint.LastBatchFingerprint);
        command.Parameters.AddWithValue("$updatedAtUtc", FormatTimestamp(checkpoint.UpdatedAtUtc));
    }

    private static void AddSourceEntryParameters(SqliteCommand command, LedgerEntry entry)
    {
        command.Parameters.AddWithValue("$ledgerEntryId", entry.EntryId);
        AddStreamParameters(command, entry.ChainId, entry.Router);
        command.Parameters.AddWithValue("$kind", FormatLedgerKind(entry.Kind));
        command.Parameters.AddWithValue("$sourceTransitionId", entry.SourceTransitionId);
        command.Parameters.AddWithValue(
            "$sourceCheckpointRevision",
            entry.SourceCheckpointRevision);
        command.Parameters.AddWithValue("$blockNumber", entry.BlockNumber);
        command.Parameters.AddWithValue("$blockHash", entry.BlockHash.Value);
        command.Parameters.AddWithValue("$transactionHash", entry.TransactionHash.Value);
        command.Parameters.AddWithValue("$logIndex", entry.LogIndex);
        command.Parameters.AddWithValue("$paymentId", entry.PaymentId.Value);
        command.Parameters.AddWithValue("$payerAddress", entry.Payer.Value);
        command.Parameters.AddWithValue("$tokenAddress", entry.Token.Value);
        command.Parameters.AddWithValue("$merchantAddress", entry.Merchant.Value);
        command.Parameters.AddWithValue("$amountRaw", entry.Amount.ToString());
        command.Parameters.AddWithValue(
            "$reversesLedgerEntryId",
            entry.ReversesEntryId is null ? DBNull.Value : entry.ReversesEntryId.Value);
        command.Parameters.AddWithValue(
            "$sourceChangedAtUtc",
            FormatTimestamp(entry.SourceChangedAtUtc));
        command.Parameters.AddWithValue(
            "$ledgerRecordedAtUtc",
            FormatTimestamp(entry.RecordedAtUtc));
    }

    private static void AddDecisionParameters(
        SqliteCommand command,
        FinalityEvaluationBatch batch,
        FinalityDecision decision,
        ChainObservationCheckpoint head)
    {
        command.Parameters.AddWithValue("$finalityRevision", decision.FinalityRevision);
        command.Parameters.AddWithValue("$kind", FormatKind(decision.Kind));
        command.Parameters.AddWithValue("$ledgerEffectEntryId", decision.LedgerEffectEntryId);
        command.Parameters.AddWithValue(
            "$revokesTransitionId",
            decision.RevokesTransitionId is null
                ? DBNull.Value
                : decision.RevokesTransitionId.Value);
        command.Parameters.AddWithValue("$headBlockNumber", head.LastBlockNumber);
        command.Parameters.AddWithValue("$headBlockHash", head.LastBlockHash.Value);
        command.Parameters.AddWithValue("$headCheckpointRevision", head.Revision);
        command.Parameters.AddWithValue("$confirmationCount", decision.ConfirmationCount);
        command.Parameters.AddWithValue(
            "$requiredConfirmationCount",
            batch.Policy.RequiredConfirmations);
        command.Parameters.AddWithValue("$reason", FormatReason(decision.Reason));
        command.Parameters.AddWithValue("$recordedAtUtc", FormatTimestamp(batch.RecordedAtUtc));
    }

    private static FinalityCommitResult ToResult(
        FinalityCommitDisposition disposition,
        FinalityCheckpoint checkpoint,
        IReadOnlyList<FinalityDecision> decisions) =>
        new(
            disposition,
            checkpoint,
            decisions.Count(item => item.Kind == FinalityTransitionKind.ConfirmationQualified),
            decisions.Count(item => item.Kind == FinalityTransitionKind.ConfirmationRevoked));

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

    private static string FormatLedgerKind(LedgerEntryKind kind) => kind switch
    {
        LedgerEntryKind.CanonicalPayment => "canonical_payment",
        LedgerEntryKind.CanonicalPaymentReversal => "canonical_payment_reversal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FormatKind(FinalityTransitionKind kind) => kind switch
    {
        FinalityTransitionKind.ConfirmationQualified => "confirmation_qualified",
        FinalityTransitionKind.ConfirmationRevoked => "confirmation_revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static FinalityTransitionKind ParseKind(string value) => value switch
    {
        "confirmation_qualified" => FinalityTransitionKind.ConfirmationQualified,
        "confirmation_revoked" => FinalityTransitionKind.ConfirmationRevoked,
        _ => throw new InvalidOperationException($"Stored finality kind '{value}' is unsupported."),
    };

    private static string FormatReason(FinalityTransitionReason reason) => reason switch
    {
        FinalityTransitionReason.ConfirmationThresholdReached =>
            "confirmation_threshold_reached",
        FinalityTransitionReason.LedgerEffectReversed => "ledger_effect_reversed",
        FinalityTransitionReason.ConfirmationThresholdLost => "confirmation_threshold_lost",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static FinalityTransitionReason ParseReason(string value) => value switch
    {
        "confirmation_threshold_reached" =>
            FinalityTransitionReason.ConfirmationThresholdReached,
        "ledger_effect_reversed" => FinalityTransitionReason.LedgerEffectReversed,
        "confirmation_threshold_lost" => FinalityTransitionReason.ConfirmationThresholdLost,
        _ => throw new InvalidOperationException($"Stored finality reason '{value}' is unsupported."),
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

    private sealed record FinalityDecision(
        long FinalityRevision,
        FinalityTransitionKind Kind,
        long LedgerEffectEntryId,
        long? RevokesTransitionId,
        long ConfirmationCount,
        FinalityTransitionReason Reason);
}
