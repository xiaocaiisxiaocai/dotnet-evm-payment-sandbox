using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Finality.Transitions;
using PaymentSandbox.Ledger.Entries;
using PaymentSandbox.Reconciliation.Evaluation;
using PaymentSandbox.Reconciliation.Reports;

namespace PaymentSandbox.Reconciliation.Persistence;

/// <summary>SQLite-backed append-only reconciliation evidence store.</summary>
public sealed class SqliteReconciliationStore(ReconciliationDatabase database) : IReconciliationStore
{
    private readonly ReconciliationDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<ReconciliationCommitResult> CommitAsync(
        ReconciliationEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        // Report summary and both copied evidence streams are one atomic unit.
        // Unknown-result retries verify those rows before returning Replayed.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        long? insertedId = await TryInsertReportAsync(connection, transaction, evaluation, cancellationToken);
        if (insertedId is not null)
        {
            await InsertEvidenceAsync(connection, transaction, insertedId.Value, evaluation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReconciliationCommitResult(
                ReconciliationCommitDisposition.Applied,
                ToReport(insertedId.Value, evaluation));
        }

        ReconciliationReport existing = await ReadBySourceKeyAsync(
            connection, transaction, evaluation, cancellationToken)
            ?? throw new InvalidOperationException("A report conflict returned no durable report.");
        if (!string.Equals(existing.BatchFingerprint, evaluation.BatchFingerprint, StringComparison.Ordinal))
        {
            throw new ReconciliationConflictException(
                "The same reconciliation source coordinates contain different facts.");
        }

        await VerifyReportAsync(connection, transaction, existing.ReportId, evaluation, cancellationToken);
        await VerifyEvidenceAsync(connection, transaction, existing.ReportId, evaluation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReconciliationCommitResult(ReconciliationCommitDisposition.Replayed, existing);
    }

    public async ValueTask<IReadOnlyList<ReconciliationReport>> GetReportsAsync(
        PaymentId paymentId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentId);
        if (maxCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = ReportSelect +
            " WHERE payment_id = $paymentId ORDER BY report_id LIMIT $maxCount;";
        command.Parameters.AddWithValue("$paymentId", paymentId.Value);
        command.Parameters.AddWithValue("$maxCount", maxCount);
        var reports = new List<ReconciliationReport>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(await ReadReportAsync(connection, transaction: null, reader, cancellationToken));
        }

        return reports;
    }

    private const string ReportSelect =
        """
        SELECT report_id, payment_id, chain_id, router_address, policy_id,
               policy_fingerprint, intent_publication_high_watermark,
               intent_publication_id, ledger_entry_high_watermark,
               ledger_checkpoint_revision, finality_transition_high_watermark,
               finality_revision, is_consistent, canonical_occurrence_count,
               active_occurrence_count, matching_active_occurrence_count,
               qualified_matching_occurrence_count, matching_active_amount_raw,
               qualified_matching_amount_raw, batch_fingerprint, evaluated_at_utc
        FROM reconciliation_reports
        """;

    private static async Task<long?> TryInsertReportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationEvaluation value,
        CancellationToken cancellationToken)
    {
        PaymentIntent? intent = value.IntentSnapshot.Intent;
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reconciliation_reports (
                payment_id, chain_id, router_address, policy_id, policy_fingerprint,
                intent_publication_high_watermark, intent_publication_id,
                intent_chain_id, intent_token_address, intent_merchant_address,
                intent_amount_raw, intent_created_at_utc,
                ledger_entry_high_watermark, ledger_checkpoint_revision,
                ledger_source_transition_id, finality_transition_high_watermark,
                finality_revision, finality_policy_fingerprint, is_consistent,
                canonical_occurrence_count, active_occurrence_count,
                matching_active_occurrence_count, qualified_matching_occurrence_count,
                matching_active_amount_raw, qualified_matching_amount_raw,
                batch_fingerprint, evaluated_at_utc)
            VALUES (
                $paymentId, $chainId, $router, $policyId, $policyFingerprint,
                $intentHigh, $intentId, $intentChain, $intentToken, $intentMerchant,
                $intentAmount, $intentCreated,
                $ledgerHigh, $ledgerRevision, $ledgerTransition,
                $finalityTransitionHigh, $finalityRevision, $finalityPolicyFingerprint,
                $consistent, $canonicalCount, $activeCount, $matchingCount,
                $qualifiedCount, $matchingAmount, $qualifiedAmount,
                $batchFingerprint, $evaluatedAt)
            ON CONFLICT (payment_id, policy_fingerprint, intent_publication_high_watermark,
                ledger_entry_high_watermark, finality_revision, finality_transition_high_watermark)
            DO NOTHING
            RETURNING report_id;
            """;
        AddReportParameters(command, value, intent);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (long)result;
    }

    private static void AddReportParameters(
        SqliteCommand command,
        ReconciliationEvaluation value,
        PaymentIntent? intent)
    {
        command.Parameters.AddWithValue("$paymentId", value.PaymentId.Value);
        command.Parameters.AddWithValue("$chainId", value.Policy.ChainId.ToString());
        command.Parameters.AddWithValue("$router", value.Policy.Router.Value);
        command.Parameters.AddWithValue("$policyId", value.Policy.PolicyId);
        command.Parameters.AddWithValue("$policyFingerprint", value.Policy.Fingerprint);
        command.Parameters.AddWithValue("$intentHigh", value.IntentSnapshot.PublicationHighWatermark);
        command.Parameters.AddWithValue("$intentId", Db(value.IntentSnapshot.PublicationId));
        command.Parameters.AddWithValue("$intentChain", Db(intent?.Terms.ChainId.ToString()));
        command.Parameters.AddWithValue("$intentToken", Db(intent?.Terms.Token.Value));
        command.Parameters.AddWithValue("$intentMerchant", Db(intent?.Terms.Merchant.Value));
        command.Parameters.AddWithValue("$intentAmount", Db(intent?.Terms.Amount.ToString()));
        command.Parameters.AddWithValue("$intentCreated", Db(intent?.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$ledgerHigh", value.LedgerSnapshot.EntryHighWatermark);
        command.Parameters.AddWithValue("$ledgerRevision", value.LedgerCheckpoint.Revision);
        command.Parameters.AddWithValue("$ledgerTransition", value.LedgerCheckpoint.LastSourceTransitionId);
        command.Parameters.AddWithValue("$finalityTransitionHigh", value.FinalitySnapshot.TransitionHighWatermark);
        command.Parameters.AddWithValue("$finalityRevision", value.FinalityCheckpoint.Revision);
        command.Parameters.AddWithValue("$finalityPolicyFingerprint", value.FinalityCheckpoint.PolicyFingerprint);
        command.Parameters.AddWithValue("$consistent", value.IsConsistent ? 1 : 0);
        command.Parameters.AddWithValue("$canonicalCount", value.CanonicalOccurrenceCount);
        command.Parameters.AddWithValue("$activeCount", value.ActiveOccurrenceCount);
        command.Parameters.AddWithValue("$matchingCount", value.MatchingActiveOccurrenceCount);
        command.Parameters.AddWithValue("$qualifiedCount", value.QualifiedMatchingOccurrenceCount);
        command.Parameters.AddWithValue("$matchingAmount", value.MatchingActiveAmount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$qualifiedAmount", value.QualifiedMatchingAmount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$batchFingerprint", value.BatchFingerprint);
        command.Parameters.AddWithValue("$evaluatedAt", value.EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        ReconciliationEvaluation value,
        CancellationToken cancellationToken)
    {
        foreach (LedgerEntry entry in value.LedgerEntries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO reconciliation_report_ledger_entries (
                    report_id, entry_id, kind, source_transition_id,
                    source_checkpoint_revision, block_number, block_hash,
                    transaction_hash, log_index, payer_address, token_address,
                    merchant_address, amount_raw, reverses_entry_id,
                    source_changed_at_utc, ledger_recorded_at_utc)
                VALUES ($report, $id, $kind, $source, $sourceRevision, $blockNumber,
                    $block, $transaction, $log, $payer, $token, $merchant,
                    $amount, $reverses, $sourceChanged, $ledgerRecorded);
                """;
            AddLedgerParameters(command, reportId, entry);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (FinalityTransition transition in value.FinalityTransitions)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO reconciliation_report_finality_transitions (
                    report_id, transition_id, finality_revision, kind,
                    ledger_effect_entry_id, revokes_transition_id, head_block_number,
                    head_block_hash, head_checkpoint_revision, confirmation_count,
                    required_confirmation_count, reason, finality_recorded_at_utc)
                VALUES ($report, $id, $revision, $kind, $effect, $revokes,
                    $headNumber, $head, $headRevision, $confirmations, $required,
                    $reason, $finalityRecorded);
                """;
            AddFinalityParameters(command, reportId, transition);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (ReconciliationDiscrepancyCode discrepancy in value.Discrepancies)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO reconciliation_report_discrepancies (report_id, code) VALUES ($report, $code);";
            command.Parameters.AddWithValue("$report", reportId);
            command.Parameters.AddWithValue("$code", FormatDiscrepancy(discrepancy));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        ReconciliationEvaluation value,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT entry_id, kind, source_transition_id, source_checkpoint_revision,
                       block_number, block_hash, transaction_hash, log_index,
                       payer_address, token_address, merchant_address, amount_raw,
                       reverses_entry_id, source_changed_at_utc, ledger_recorded_at_utc
                FROM reconciliation_report_ledger_entries
                WHERE report_id = $report ORDER BY entry_id;
                """;
            command.Parameters.AddWithValue("$report", reportId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            foreach (LedgerEntry expected in value.LedgerEntries)
            {
                if (!await reader.ReadAsync(cancellationToken) ||
                    reader.GetInt64(0) != expected.EntryId || reader.GetString(1) != FormatLedgerKind(expected.Kind) ||
                    reader.GetInt64(2) != expected.SourceTransitionId || reader.GetInt64(3) != expected.SourceCheckpointRevision ||
                    reader.GetInt64(4) != expected.BlockNumber || reader.GetString(5) != expected.BlockHash.Value ||
                    reader.GetString(6) != expected.TransactionHash.Value || reader.GetInt64(7) != expected.LogIndex ||
                    reader.GetString(8) != expected.Payer.Value || reader.GetString(9) != expected.Token.Value ||
                    reader.GetString(10) != expected.Merchant.Value || reader.GetString(11) != expected.Amount.ToString() ||
                    ReadNullable(reader, 12) != expected.ReversesEntryId ||
                    ParseTimestamp(reader.GetString(13)) != expected.SourceChangedAtUtc ||
                    ParseTimestamp(reader.GetString(14)) != expected.RecordedAtUtc)
                {
                    throw new ReconciliationConflictException("Durable Ledger reconciliation evidence changed.");
                }
            }

            if (await reader.ReadAsync(cancellationToken))
            {
                throw new ReconciliationConflictException("Durable Ledger reconciliation evidence has extra rows.");
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT transition_id, finality_revision, kind, ledger_effect_entry_id,
                       revokes_transition_id, head_block_number, head_block_hash,
                       head_checkpoint_revision, confirmation_count,
                       required_confirmation_count, reason, finality_recorded_at_utc
                FROM reconciliation_report_finality_transitions
                WHERE report_id = $report ORDER BY transition_id;
                """;
            command.Parameters.AddWithValue("$report", reportId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            foreach (FinalityTransition expected in value.FinalityTransitions)
            {
                if (!await reader.ReadAsync(cancellationToken) ||
                    reader.GetInt64(0) != expected.TransitionId || reader.GetInt64(1) != expected.FinalityRevision ||
                    reader.GetString(2) != FormatFinalityKind(expected.Kind) ||
                    reader.GetInt64(3) != expected.LedgerEffectEntryId || ReadNullable(reader, 4) != expected.RevokesTransitionId ||
                    reader.GetInt64(5) != expected.HeadBlockNumber || reader.GetString(6) != expected.HeadBlockHash.Value ||
                    reader.GetInt64(7) != expected.HeadCheckpointRevision || reader.GetInt64(8) != expected.ConfirmationCount ||
                    reader.GetInt64(9) != expected.RequiredConfirmationCount || reader.GetString(10) != FormatReason(expected.Reason) ||
                    ParseTimestamp(reader.GetString(11)) != expected.RecordedAtUtc)
                {
                    throw new ReconciliationConflictException("Durable Finality reconciliation evidence changed.");
                }
            }

            if (await reader.ReadAsync(cancellationToken))
            {
                throw new ReconciliationConflictException("Durable Finality reconciliation evidence has extra rows.");
            }
        }

        IReadOnlyList<ReconciliationDiscrepancyCode> durable = await ReadDiscrepanciesAsync(
            connection, transaction, reportId, cancellationToken);
        if (!durable.SequenceEqual(value.Discrepancies))
        {
            throw new ReconciliationConflictException("Durable discrepancy evidence changed.");
        }
    }

    private static async Task VerifyReportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        ReconciliationEvaluation value,
        CancellationToken cancellationToken)
    {
        PaymentIntent? intent = value.IntentSnapshot.Intent;
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT payment_id, chain_id, router_address, policy_id, policy_fingerprint,
                   intent_publication_high_watermark, intent_publication_id,
                   intent_chain_id, intent_token_address, intent_merchant_address,
                   intent_amount_raw, intent_created_at_utc, ledger_entry_high_watermark,
                   ledger_checkpoint_revision, ledger_source_transition_id,
                   finality_transition_high_watermark, finality_revision,
                   finality_policy_fingerprint, is_consistent,
                   canonical_occurrence_count, active_occurrence_count,
                   matching_active_occurrence_count, qualified_matching_occurrence_count,
                   matching_active_amount_raw, qualified_matching_amount_raw,
                   batch_fingerprint
            FROM reconciliation_reports WHERE report_id = $report;
            """;
        command.Parameters.AddWithValue("$report", reportId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        bool matches = await reader.ReadAsync(cancellationToken) &&
            reader.GetString(0) == value.PaymentId.Value &&
            reader.GetString(1) == value.Policy.ChainId.ToString() &&
            reader.GetString(2) == value.Policy.Router.Value &&
            reader.GetString(3) == value.Policy.PolicyId &&
            reader.GetString(4) == value.Policy.Fingerprint &&
            reader.GetInt64(5) == value.IntentSnapshot.PublicationHighWatermark &&
            ReadNullable(reader, 6) == value.IntentSnapshot.PublicationId &&
            ReadNullableString(reader, 7) == intent?.Terms.ChainId.ToString() &&
            ReadNullableString(reader, 8) == intent?.Terms.Token.Value &&
            ReadNullableString(reader, 9) == intent?.Terms.Merchant.Value &&
            ReadNullableString(reader, 10) == intent?.Terms.Amount.ToString() &&
            ReadNullableString(reader, 11) == intent?.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
            reader.GetInt64(12) == value.LedgerSnapshot.EntryHighWatermark &&
            reader.GetInt64(13) == value.LedgerCheckpoint.Revision &&
            reader.GetInt64(14) == value.LedgerCheckpoint.LastSourceTransitionId &&
            reader.GetInt64(15) == value.FinalitySnapshot.TransitionHighWatermark &&
            reader.GetInt64(16) == value.FinalityCheckpoint.Revision &&
            reader.GetString(17) == value.FinalityCheckpoint.PolicyFingerprint &&
            reader.GetInt64(18) == (value.IsConsistent ? 1 : 0) &&
            reader.GetInt32(19) == value.CanonicalOccurrenceCount &&
            reader.GetInt32(20) == value.ActiveOccurrenceCount &&
            reader.GetInt32(21) == value.MatchingActiveOccurrenceCount &&
            reader.GetInt32(22) == value.QualifiedMatchingOccurrenceCount &&
            reader.GetString(23) == value.MatchingActiveAmount.ToString(CultureInfo.InvariantCulture) &&
            reader.GetString(24) == value.QualifiedMatchingAmount.ToString(CultureInfo.InvariantCulture) &&
            reader.GetString(25) == value.BatchFingerprint;
        if (!matches || await reader.ReadAsync(cancellationToken))
        {
            throw new ReconciliationConflictException("Durable reconciliation report fields changed.");
        }
    }

    private static async Task<ReconciliationReport?> ReadBySourceKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationEvaluation value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReportSelect +
            """
             WHERE payment_id = $paymentId AND policy_fingerprint = $policy
               AND intent_publication_high_watermark = $intentHigh
               AND ledger_entry_high_watermark = $ledgerHigh
               AND finality_revision = $finalityRevision
               AND finality_transition_high_watermark = $transitionHigh;
            """;
        command.Parameters.AddWithValue("$paymentId", value.PaymentId.Value);
        command.Parameters.AddWithValue("$policy", value.Policy.Fingerprint);
        command.Parameters.AddWithValue("$intentHigh", value.IntentSnapshot.PublicationHighWatermark);
        command.Parameters.AddWithValue("$ledgerHigh", value.LedgerSnapshot.EntryHighWatermark);
        command.Parameters.AddWithValue("$finalityRevision", value.FinalityCheckpoint.Revision);
        command.Parameters.AddWithValue("$transitionHigh", value.FinalitySnapshot.TransitionHighWatermark);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadReportAsync(connection, transaction, reader, cancellationToken)
            : null;
    }

    private static async Task<ReconciliationReport> ReadReportAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        long reportId = reader.GetInt64(0);
        IReadOnlyList<ReconciliationDiscrepancyCode> discrepancies = await ReadDiscrepanciesAsync(
            connection, transaction, reportId, cancellationToken);
        return new ReconciliationReport(
            reportId, PaymentId.Parse(reader.GetString(1)), EvmChainId.Parse(reader.GetString(2)),
            EvmAddress.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), ReadNullable(reader, 7), reader.GetInt64(8), reader.GetInt64(9),
            reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12) == 1,
            reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
            BigInteger.Parse(reader.GetString(17), CultureInfo.InvariantCulture),
            BigInteger.Parse(reader.GetString(18), CultureInfo.InvariantCulture), discrepancies,
            reader.GetString(19), ParseTimestamp(reader.GetString(20)));
    }

    private static async Task<IReadOnlyList<ReconciliationDiscrepancyCode>> ReadDiscrepanciesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long reportId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT code FROM reconciliation_report_discrepancies WHERE report_id = $report ORDER BY code;";
        command.Parameters.AddWithValue("$report", reportId);
        var values = new List<ReconciliationDiscrepancyCode>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(ParseDiscrepancy(reader.GetString(0)));
        return values.Order().ToArray();
    }

    private static ReconciliationReport ToReport(long id, ReconciliationEvaluation value) =>
        new(id, value.PaymentId, value.Policy.ChainId, value.Policy.Router,
            value.Policy.PolicyId, value.Policy.Fingerprint,
            value.IntentSnapshot.PublicationHighWatermark, value.IntentSnapshot.PublicationId,
            value.LedgerSnapshot.EntryHighWatermark, value.LedgerCheckpoint.Revision,
            value.FinalitySnapshot.TransitionHighWatermark, value.FinalityCheckpoint.Revision,
            value.IsConsistent, value.CanonicalOccurrenceCount, value.ActiveOccurrenceCount,
            value.MatchingActiveOccurrenceCount, value.QualifiedMatchingOccurrenceCount,
            value.MatchingActiveAmount, value.QualifiedMatchingAmount, value.Discrepancies,
            value.BatchFingerprint, value.EvaluatedAtUtc);

    private static void AddLedgerParameters(SqliteCommand command, long reportId, LedgerEntry value)
    {
        command.Parameters.AddWithValue("$report", reportId); command.Parameters.AddWithValue("$id", value.EntryId);
        command.Parameters.AddWithValue("$kind", FormatLedgerKind(value.Kind));
        command.Parameters.AddWithValue("$source", value.SourceTransitionId); command.Parameters.AddWithValue("$sourceRevision", value.SourceCheckpointRevision);
        command.Parameters.AddWithValue("$blockNumber", value.BlockNumber); command.Parameters.AddWithValue("$block", value.BlockHash.Value);
        command.Parameters.AddWithValue("$transaction", value.TransactionHash.Value); command.Parameters.AddWithValue("$log", value.LogIndex);
        command.Parameters.AddWithValue("$payer", value.Payer.Value); command.Parameters.AddWithValue("$token", value.Token.Value);
        command.Parameters.AddWithValue("$merchant", value.Merchant.Value); command.Parameters.AddWithValue("$amount", value.Amount.ToString());
        command.Parameters.AddWithValue("$reverses", Db(value.ReversesEntryId));
        command.Parameters.AddWithValue("$sourceChanged", value.SourceChangedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ledgerRecorded", value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void AddFinalityParameters(SqliteCommand command, long reportId, FinalityTransition value)
    {
        command.Parameters.AddWithValue("$report", reportId); command.Parameters.AddWithValue("$id", value.TransitionId);
        command.Parameters.AddWithValue("$revision", value.FinalityRevision); command.Parameters.AddWithValue("$kind", FormatFinalityKind(value.Kind));
        command.Parameters.AddWithValue("$effect", value.LedgerEffectEntryId); command.Parameters.AddWithValue("$revokes", Db(value.RevokesTransitionId));
        command.Parameters.AddWithValue("$headNumber", value.HeadBlockNumber); command.Parameters.AddWithValue("$head", value.HeadBlockHash.Value);
        command.Parameters.AddWithValue("$headRevision", value.HeadCheckpointRevision); command.Parameters.AddWithValue("$confirmations", value.ConfirmationCount);
        command.Parameters.AddWithValue("$required", value.RequiredConfirmationCount); command.Parameters.AddWithValue("$reason", FormatReason(value.Reason));
        command.Parameters.AddWithValue("$finalityRecorded", value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static long? ReadNullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string FormatLedgerKind(LedgerEntryKind value) => value switch
    {
        LedgerEntryKind.CanonicalPayment => "canonical_payment",
        LedgerEntryKind.CanonicalPaymentReversal => "canonical_payment_reversal",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string FormatFinalityKind(FinalityTransitionKind value) => value switch
    {
        FinalityTransitionKind.ConfirmationQualified => "confirmation_qualified",
        FinalityTransitionKind.ConfirmationRevoked => "confirmation_revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string FormatReason(FinalityTransitionReason value) => value switch
    {
        FinalityTransitionReason.ConfirmationThresholdReached => "confirmation_threshold_reached",
        FinalityTransitionReason.LedgerEffectReversed => "ledger_effect_reversed",
        FinalityTransitionReason.ConfirmationThresholdLost => "confirmation_threshold_lost",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string FormatDiscrepancy(ReconciliationDiscrepancyCode value) => value switch
    {
        ReconciliationDiscrepancyCode.IntentMissing => "intent_missing",
        ReconciliationDiscrepancyCode.ActivePaymentMissing => "active_payment_missing",
        ReconciliationDiscrepancyCode.ReversedPaymentHistory => "reversed_payment_history",
        ReconciliationDiscrepancyCode.ChainMismatch => "chain_mismatch",
        ReconciliationDiscrepancyCode.TokenMismatch => "token_mismatch",
        ReconciliationDiscrepancyCode.MerchantMismatch => "merchant_mismatch",
        ReconciliationDiscrepancyCode.AmountUnderpaid => "amount_underpaid",
        ReconciliationDiscrepancyCode.AmountOverpaid => "amount_overpaid",
        ReconciliationDiscrepancyCode.QualificationIncomplete => "qualification_incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static ReconciliationDiscrepancyCode ParseDiscrepancy(string value) => value switch
    {
        "intent_missing" => ReconciliationDiscrepancyCode.IntentMissing,
        "active_payment_missing" => ReconciliationDiscrepancyCode.ActivePaymentMissing,
        "reversed_payment_history" => ReconciliationDiscrepancyCode.ReversedPaymentHistory,
        "chain_mismatch" => ReconciliationDiscrepancyCode.ChainMismatch,
        "token_mismatch" => ReconciliationDiscrepancyCode.TokenMismatch,
        "merchant_mismatch" => ReconciliationDiscrepancyCode.MerchantMismatch,
        "amount_underpaid" => ReconciliationDiscrepancyCode.AmountUnderpaid,
        "amount_overpaid" => ReconciliationDiscrepancyCode.AmountOverpaid,
        "qualification_incomplete" => ReconciliationDiscrepancyCode.QualificationIncomplete,
        _ => throw new InvalidOperationException($"Unsupported discrepancy code '{value}'."),
    };
}
