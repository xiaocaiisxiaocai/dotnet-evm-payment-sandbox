using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Persistence;

/// <summary>SQLite-backed append-only transaction lifecycle and nonce allocator.</summary>
public sealed class SqliteTransactionLifecycleStore(TransactionLifecycleDatabase database)
    : ITransactionLifecycleStore
{
    private readonly TransactionLifecycleDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<LifecycleCommitResult> ReserveAsync(
        PreparedPaymentOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        // An immediate transaction serializes nonce selection before any read.
        // A second writer therefore observes the first writer's reservation.
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        OperationRow? existing = await ReadOperationAsync(
            connection, transaction, operation.Request.OperationId, cancellationToken);
        if (existing is not null)
        {
            VerifyOperation(existing, operation);
            TransactionLifecycleSnapshot replay = ToSnapshot(existing);
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleCommitResult(LifecycleCommitDisposition.Replayed, replay);
        }

        long nonce = await SelectNonceAsync(connection, transaction, operation, cancellationToken);
        await InsertOperationAsync(connection, transaction, operation, nonce, cancellationToken);
        OperationRow inserted = await ReadOperationAsync(
            connection, transaction, operation.Request.OperationId, cancellationToken)
            ?? throw new InvalidOperationException("The inserted transaction operation was not readable.");
        await transaction.CommitAsync(cancellationToken);
        return new LifecycleCommitResult(LifecycleCommitDisposition.Applied, ToSnapshot(inserted));
    }

    public async ValueTask<TransactionLifecycleSnapshot?> GetAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        OperationRow? row = await ReadOperationAsync(connection, null, operationId, cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    public async ValueTask<IReadOnlyList<TransactionAttemptSummary>> GetAttemptsAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        OperationRow operation = await RequireOperationAsync(
            connection, null, operationId, cancellationToken);
        IReadOnlyList<TransactionAttemptPayload> payloads = await ReadPayloadsAsync(
            connection, null, operation, cancellationToken);
        return payloads.Select(item => item.Summary).ToArray();
    }

    public async ValueTask<TransactionAttemptPayload?> GetCurrentPayloadAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        OperationRow operation = await RequireOperationAsync(
            connection, null, operationId, cancellationToken);
        IReadOnlyList<TransactionAttemptPayload> values = await ReadPayloadsAsync(
            connection, null, operation, cancellationToken);
        return values.LastOrDefault();
    }

    public async ValueTask<IReadOnlyList<TransactionAttemptPayload>> GetPayloadsAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        OperationRow operation = await RequireOperationAsync(
            connection, null, operationId, cancellationToken);
        return await ReadPayloadsAsync(connection, null, operation, cancellationToken);
    }

    public async ValueTask<LifecycleCommitResult> CommitAttemptAsync(
        PreparedTransactionAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        OperationRow operation = await RequireOperationAsync(
            connection, transaction, attempt.OperationId, cancellationToken);
        if (operation.ReceiptStatus is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, ToSnapshot(operation));
        }

        IReadOnlyList<TransactionAttemptPayload> current = await ReadPayloadsAsync(
            connection, transaction, operation, cancellationToken);
        if (current.Count == attempt.ExpectedPreviousAttemptCount + 1)
        {
            VerifyAttempt(current[^1], attempt);
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleCommitResult(LifecycleCommitDisposition.Replayed, ToSnapshot(operation));
        }

        if (current.Count != attempt.ExpectedPreviousAttemptCount)
        {
            throw new TransactionLifecycleConflictException(
                "The transaction attempt history changed before this attempt was committed.");
        }

        if (current.Count >= operation.MaxAttempts)
        {
            throw new TransactionLifecycleConflictException(
                "The transaction lifecycle reached its durable attempt limit.");
        }

        ValidateAttemptFee(operation, current.LastOrDefault()?.Summary.Fee, attempt.Fee);
        await InsertAttemptAsync(
            connection, transaction, operation, attempt, current.Count + 1, cancellationToken);
        OperationRow updated = await RequireOperationAsync(
            connection, transaction, attempt.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LifecycleCommitResult(LifecycleCommitDisposition.Applied, ToSnapshot(updated));
    }

    public async ValueTask<LifecycleCommitResult> AppendBroadcastAsync(
        BroadcastObservationCommand observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        OperationRow operation = await RequireOperationAsync(
            connection, transaction, observation.OperationId, cancellationToken);
        if (operation.ReceiptStatus is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, ToSnapshot(operation));
        }

        TransactionAttemptPayload current = (await ReadPayloadsAsync(
            connection, transaction, operation, cancellationToken)).LastOrDefault()
            ?? throw new TransactionLifecycleConflictException("No signed attempt exists to broadcast.");
        if (current.Summary.AttemptId != observation.AttemptId ||
            current.Summary.TransactionHash != observation.TransactionHash)
        {
            throw new TransactionLifecycleConflictException(
                "Only the current persisted transaction attempt may record a broadcast.");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transaction_broadcast_observations (
                attempt_id, outcome, code, observed_at_utc)
            VALUES ($attempt, $outcome, $code, $time);
            """;
        command.Parameters.AddWithValue("$attempt", observation.AttemptId);
        command.Parameters.AddWithValue("$outcome", FormatBroadcastOutcome(observation.Outcome.Kind));
        command.Parameters.AddWithValue("$code", observation.Outcome.Code);
        command.Parameters.AddWithValue("$time", FormatTimestamp(observation.ObservedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        OperationRow updated = await RequireOperationAsync(
            connection, transaction, observation.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LifecycleCommitResult(LifecycleCommitDisposition.Applied, ToSnapshot(updated));
    }

    public async ValueTask<LifecycleCommitResult> AppendReceiptAsync(
        ReceiptObservationCommand observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        OperationRow operation = await RequireOperationAsync(
            connection, transaction, observation.OperationId, cancellationToken);
        IReadOnlyList<TransactionAttemptPayload> attempts = await ReadPayloadsAsync(
            connection, transaction, operation, cancellationToken);
        TransactionAttemptPayload attempt = attempts.SingleOrDefault(
            item => item.Summary.AttemptId == observation.AttemptId)
            ?? throw new TransactionLifecycleConflictException("The receipt attempt is not part of this operation.");
        VerifyReceiptAgainstAttempt(operation, attempt, observation.Receipt);

        if (operation.ReceiptStatus is not null)
        {
            VerifyDurableReceipt(operation, attempt, observation.Receipt);
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleCommitResult(LifecycleCommitDisposition.Replayed, ToSnapshot(operation));
        }

        await InsertReceiptAsync(connection, transaction, observation, cancellationToken);
        OperationRow updated = await RequireOperationAsync(
            connection, transaction, observation.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LifecycleCommitResult(LifecycleCommitDisposition.Applied, ToSnapshot(updated));
    }

    private static async Task<long> SelectNonceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedPaymentOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.ObservedPendingNonce < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT MAX(nonce) FROM transaction_operations WHERE chain_id = $chain AND signer_address = $signer;";
        command.Parameters.AddWithValue("$chain", operation.Policy.ChainId.ToString());
        command.Parameters.AddWithValue("$signer", operation.Policy.Signer.Value);
        object? scalar = await command.ExecuteScalarAsync(cancellationToken);
        long nonce = scalar is null or DBNull
            ? operation.ObservedPendingNonce
            : Math.Max(operation.ObservedPendingNonce, checked((long)scalar + 1));
        if (nonce - operation.ObservedPendingNonce > operation.Policy.MaxReservedNonceLead)
        {
            throw new TransactionLifecycleConflictException(
                "The local reserved nonce lead exceeds the lifecycle policy.");
        }

        return nonce;
    }

    private static async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedPaymentOperation value,
        long nonce,
        CancellationToken cancellationToken)
    {
        PaymentTransactionRequest request = value.Request;
        TransactionLifecyclePolicy policy = value.Policy;
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transaction_operations (
                operation_id, request_fingerprint, chain_id, signer_address,
                router_address, payment_id, token_address, merchant_address,
                amount_raw, nonce, observed_pending_nonce, gas_limit, calldata,
                policy_id, policy_fingerprint, max_gas_limit,
                initial_max_fee_per_gas_wei, initial_max_priority_fee_per_gas_wei,
                max_fee_per_gas_wei, max_priority_fee_per_gas_wei,
                minimum_fee_bump_basis_points, max_attempts,
                max_reserved_nonce_lead, created_at_utc)
            VALUES (
                $operation, $requestFingerprint, $chain, $signer, $router,
                $payment, $token, $merchant, $amount, $nonce, $observedNonce,
                $gas, $calldata, $policyId, $policyFingerprint, $maxGas,
                $initialMaxFee, $initialPriorityFee, $maxFee, $maxPriority,
                $bump, $maxAttempts, $maxLead, $created);
            """;
        command.Parameters.AddWithValue("$operation", request.OperationId.Value);
        command.Parameters.AddWithValue("$requestFingerprint", value.RequestFingerprint);
        command.Parameters.AddWithValue("$chain", policy.ChainId.ToString());
        command.Parameters.AddWithValue("$signer", policy.Signer.Value);
        command.Parameters.AddWithValue("$router", policy.Router.Value);
        command.Parameters.AddWithValue("$payment", request.PaymentId.Value);
        command.Parameters.AddWithValue("$token", request.Token.Value);
        command.Parameters.AddWithValue("$merchant", request.Merchant.Value);
        command.Parameters.AddWithValue("$amount", request.Amount.ToString());
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$observedNonce", value.ObservedPendingNonce);
        command.Parameters.AddWithValue("$gas", request.GasLimit);
        command.Parameters.AddWithValue("$calldata", value.Calldata);
        command.Parameters.AddWithValue("$policyId", policy.PolicyId);
        command.Parameters.AddWithValue("$policyFingerprint", policy.Fingerprint);
        command.Parameters.AddWithValue("$maxGas", policy.MaxGasLimit);
        command.Parameters.AddWithValue("$initialMaxFee", request.InitialFee.MaxFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$initialPriorityFee", request.InitialFee.MaxPriorityFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maxFee", policy.MaxFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maxPriority", policy.MaxPriorityFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$bump", policy.MinimumReplacementFeeBumpBasisPoints);
        command.Parameters.AddWithValue("$maxAttempts", policy.MaxAttemptsPerOperation);
        command.Parameters.AddWithValue("$maxLead", policy.MaxReservedNonceLead);
        command.Parameters.AddWithValue("$created", FormatTimestamp(value.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationRow operation,
        PreparedTransactionAttempt value,
        int sequence,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transaction_attempts (
                operation_id, sequence, nonce, max_fee_per_gas_wei,
                max_priority_fee_per_gas_wei, raw_transaction, transaction_hash,
                signed_byte_length, unsigned_fingerprint, created_at_utc)
            VALUES ($operation, $sequence, $nonce, $maxFee, $priorityFee,
                $raw, $hash, $length, $unsignedFingerprint, $created);
            """;
        command.Parameters.AddWithValue("$operation", value.OperationId.Value);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$nonce", operation.Nonce);
        command.Parameters.AddWithValue("$maxFee", value.Fee.MaxFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$priorityFee", value.Fee.MaxPriorityFeePerGasWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$raw", value.Payload.RawTransaction);
        command.Parameters.AddWithValue("$hash", value.Payload.TransactionHash.Value);
        command.Parameters.AddWithValue("$length", value.Payload.ByteLength);
        command.Parameters.AddWithValue("$unsignedFingerprint", value.UnsignedFingerprint);
        command.Parameters.AddWithValue("$created", FormatTimestamp(value.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReceiptObservationCommand value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transaction_receipt_observations (
                operation_id, attempt_id, transaction_hash, execution_status,
                block_number, block_hash, gas_used, effective_gas_price_wei,
                observed_at_utc)
            VALUES ($operation, $attempt, $hash, $status, $blockNumber,
                $blockHash, $gasUsed, $gasPrice, $time);
            """;
        command.Parameters.AddWithValue("$operation", value.OperationId.Value);
        command.Parameters.AddWithValue("$attempt", value.AttemptId);
        command.Parameters.AddWithValue("$hash", value.Receipt.TransactionHash.Value);
        command.Parameters.AddWithValue("$status", FormatExecutionStatus(value.Receipt.Status));
        command.Parameters.AddWithValue("$blockNumber", value.Receipt.BlockNumber);
        command.Parameters.AddWithValue("$blockHash", value.Receipt.BlockHash.Value);
        command.Parameters.AddWithValue("$gasUsed", value.Receipt.GasUsed);
        command.Parameters.AddWithValue("$gasPrice", value.Receipt.EffectiveGasPriceWei.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$time", FormatTimestamp(value.ObservedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<OperationRow> RequireOperationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransactionOperationId operationId,
        CancellationToken cancellationToken) =>
        await ReadOperationAsync(connection, transaction, operationId, cancellationToken)
        ?? throw new KeyNotFoundException($"Transaction operation '{operationId.Value}' was not found.");

    private static async Task<OperationRow?> ReadOperationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransactionOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT operation.operation_id, operation.request_fingerprint,
                   operation.chain_id, operation.signer_address,
                   operation.router_address, operation.payment_id,
                   operation.token_address, operation.merchant_address,
                   operation.amount_raw, operation.nonce,
                   operation.observed_pending_nonce, operation.gas_limit,
                   operation.calldata, operation.policy_id,
                   operation.policy_fingerprint, operation.max_gas_limit,
                   operation.initial_max_fee_per_gas_wei,
                   operation.initial_max_priority_fee_per_gas_wei,
                   operation.max_fee_per_gas_wei,
                   operation.max_priority_fee_per_gas_wei,
                   operation.minimum_fee_bump_basis_points,
                   operation.max_attempts, operation.max_reserved_nonce_lead,
                   operation.created_at_utc,
                   (SELECT COUNT(*) FROM transaction_attempts AS attempt
                    WHERE attempt.operation_id = operation.operation_id),
                   (SELECT COUNT(*) FROM transaction_broadcast_observations AS broadcast
                    JOIN transaction_attempts AS attempt ON attempt.attempt_id = broadcast.attempt_id
                    WHERE attempt.operation_id = operation.operation_id),
                   (SELECT attempt.transaction_hash FROM transaction_attempts AS attempt
                    WHERE attempt.operation_id = operation.operation_id
                    ORDER BY attempt.sequence DESC LIMIT 1),
                   (SELECT broadcast.outcome
                    FROM transaction_broadcast_observations AS broadcast
                    JOIN transaction_attempts AS attempt ON attempt.attempt_id = broadcast.attempt_id
                    WHERE attempt.operation_id = operation.operation_id
                      AND attempt.sequence = (SELECT MAX(latest.sequence)
                          FROM transaction_attempts AS latest
                          WHERE latest.operation_id = operation.operation_id)
                    ORDER BY CASE WHEN broadcast.outcome IN ('accepted', 'already_known')
                                  THEN 0 ELSE 1 END,
                             broadcast.broadcast_id DESC LIMIT 1),
                   receipt.transaction_hash, receipt.execution_status,
                   receipt.block_number, receipt.block_hash, receipt.gas_used,
                   receipt.effective_gas_price_wei
            FROM transaction_operations AS operation
            LEFT JOIN transaction_receipt_observations AS receipt
              ON receipt.operation_id = operation.operation_id
            WHERE operation.operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var operation = new OperationRow(
            TransactionOperationId.Parse(reader.GetString(0)), reader.GetString(1),
            EvmChainId.Parse(reader.GetString(2)), EvmAddress.Parse(reader.GetString(3)),
            EvmAddress.Parse(reader.GetString(4)), PaymentId.Parse(reader.GetString(5)),
            EvmAddress.Parse(reader.GetString(6)), EvmAddress.Parse(reader.GetString(7)),
            new RawTokenAmount(BigInteger.Parse(reader.GetString(8), CultureInfo.InvariantCulture)),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetString(12),
            reader.GetString(13), reader.GetString(14), reader.GetInt64(15),
            ParseInteger(reader.GetString(16)), ParseInteger(reader.GetString(17)),
            ParseInteger(reader.GetString(18)), ParseInteger(reader.GetString(19)),
            reader.GetInt32(20), reader.GetInt32(21), reader.GetInt32(22),
            ParseTimestamp(reader.GetString(23)), reader.GetInt32(24), reader.GetInt32(25),
            ReadHash(reader, 26), ReadNullableString(reader, 27), ReadHash(reader, 28),
            ReadNullableString(reader, 29), ReadNullableInt64(reader, 30), ReadHash(reader, 31),
            ReadNullableInt64(reader, 32), ReadNullableInteger(reader, 33));
        ValidateDurableOperation(operation);
        return operation;
    }

    private static async Task<IReadOnlyList<TransactionAttemptPayload>> ReadPayloadsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationRow operation,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT attempt_id, sequence, nonce, max_fee_per_gas_wei,
                   max_priority_fee_per_gas_wei, raw_transaction,
                   transaction_hash, signed_byte_length, unsigned_fingerprint,
                   created_at_utc,
                   (SELECT COUNT(*) FROM transaction_broadcast_observations AS broadcast
                    WHERE broadcast.attempt_id = attempt.attempt_id),
                   (SELECT outcome FROM transaction_broadcast_observations AS broadcast
                    WHERE broadcast.attempt_id = attempt.attempt_id
                    -- Once any endpoint accepted the exact bytes, a later
                    -- timeout/rejection cannot erase that positive evidence.
                    ORDER BY CASE WHEN outcome IN ('accepted', 'already_known')
                                  THEN 0 ELSE 1 END,
                             broadcast.broadcast_id DESC LIMIT 1)
            FROM transaction_attempts AS attempt
            WHERE operation_id = $operation ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$operation", operation.OperationId.Value);
        var values = new List<TransactionAttemptPayload>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = new SignedTransactionPayload(reader.GetString(5));
            TransactionHash storedHash = TransactionHash.Parse(reader.GetString(6));
            int storedLength = reader.GetInt32(7);
            if (payload.TransactionHash != storedHash || payload.ByteLength != storedLength)
            {
                throw new TransactionLifecycleConflictException(
                    "Stored signed transaction bytes do not match their durable identity.");
            }

            values.Add(new TransactionAttemptPayload(
                new TransactionAttemptSummary(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2),
                    new TransactionFeeQuote(ParseInteger(reader.GetString(3)), ParseInteger(reader.GetString(4))),
                    storedHash, storedLength, reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : ParseBroadcastOutcome(reader.GetString(11)),
                    ParseTimestamp(reader.GetString(9))),
                payload, reader.GetString(8)));
        }

        ValidateDurableAttemptHistory(operation, values);
        return values;
    }

    /// <summary>
    /// Rebuilds every intended unsigned transaction from immutable operation
    /// facts. This turns the stored fingerprint into an active tamper check,
    /// rather than metadata that is trusted merely because it exists.
    /// </summary>
    private static void ValidateDurableAttemptHistory(
        OperationRow operation,
        IReadOnlyList<TransactionAttemptPayload> attempts)
    {
        if (attempts.Count > operation.MaxAttempts)
        {
            throw new TransactionLifecycleConflictException(
                "The durable transaction attempt history exceeds its policy limit.");
        }

        TransactionFeeQuote? previous = null;
        for (int index = 0; index < attempts.Count; index++)
        {
            TransactionAttemptPayload attempt = attempts[index];
            if (attempt.Summary.Sequence != index + 1 ||
                attempt.Summary.Nonce != operation.Nonce)
            {
                throw new TransactionLifecycleConflictException(
                    "The durable transaction attempt sequence or nonce was modified.");
            }

            ValidateAttemptFee(operation, previous, attempt.Summary.Fee);
            var unsigned = new UnsignedPaymentTransaction(
                operation.ChainId, operation.Signer, operation.Router,
                operation.Nonce, operation.GasLimit,
                attempt.Summary.Fee.MaxFeePerGasWei,
                attempt.Summary.Fee.MaxPriorityFeePerGasWei,
                operation.Calldata);
            if (!string.Equals(
                    attempt.UnsignedFingerprint,
                    TransactionLifecycleFingerprint.ForUnsigned(unsigned),
                    StringComparison.Ordinal))
            {
                throw new TransactionLifecycleConflictException(
                    "The durable unsigned transaction facts do not match their fingerprint.");
            }

            previous = attempt.Summary.Fee;
        }
    }

    private static void VerifyOperation(OperationRow durable, PreparedPaymentOperation expected)
    {
        PaymentTransactionRequest request = expected.Request;
        TransactionLifecyclePolicy policy = expected.Policy;
        bool matches = durable.RequestFingerprint == expected.RequestFingerprint &&
            durable.ChainId == policy.ChainId && durable.Signer == policy.Signer &&
            durable.Router == policy.Router && durable.PaymentId == request.PaymentId &&
            durable.Token == request.Token && durable.Merchant == request.Merchant &&
            durable.Amount == request.Amount && durable.GasLimit == request.GasLimit &&
            durable.Calldata == expected.Calldata && durable.PolicyId == policy.PolicyId &&
            durable.PolicyFingerprint == policy.Fingerprint && durable.MaxGasLimit == policy.MaxGasLimit &&
            durable.InitialMaxFee == request.InitialFee.MaxFeePerGasWei &&
            durable.InitialPriorityFee == request.InitialFee.MaxPriorityFeePerGasWei &&
            durable.MaxFee == policy.MaxFeePerGasWei && durable.MaxPriorityFee == policy.MaxPriorityFeePerGasWei &&
            durable.MinimumFeeBumpBasisPoints == policy.MinimumReplacementFeeBumpBasisPoints &&
            durable.MaxAttempts == policy.MaxAttemptsPerOperation &&
            durable.MaxReservedNonceLead == policy.MaxReservedNonceLead;
        if (!matches)
        {
            throw new TransactionLifecycleConflictException(
                "The operation ID is already bound to different transaction facts.");
        }
    }

    /// <summary>
    /// Reconstructs the policy and request from one durable row. This catches a
    /// partial database edit even on ordinary reads that are not create-retries.
    /// The hashes are integrity checks, not keyed tamper-proof signatures.
    /// </summary>
    private static void ValidateDurableOperation(OperationRow operation)
    {
        var policy = new TransactionLifecyclePolicy(
            operation.ChainId, operation.Router, operation.Signer,
            operation.PolicyId, operation.MaxGasLimit, operation.MaxFee,
            operation.MaxPriorityFee, operation.MinimumFeeBumpBasisPoints,
            operation.MaxAttempts, operation.MaxReservedNonceLead);
        var request = new PaymentTransactionRequest(
            operation.OperationId, operation.PaymentId, operation.Token,
            operation.Merchant, operation.Amount, operation.GasLimit,
            new TransactionFeeQuote(
                operation.InitialMaxFee, operation.InitialPriorityFee));

        if (!string.Equals(policy.Fingerprint, operation.PolicyFingerprint, StringComparison.Ordinal) ||
            !string.Equals(
                TransactionLifecycleFingerprint.ForRequest(policy, request, operation.Calldata),
                operation.RequestFingerprint,
                StringComparison.Ordinal))
        {
            throw new TransactionLifecycleConflictException(
                "The durable transaction operation does not match its policy or request fingerprint.");
        }
    }

    private static void VerifyAttempt(
        TransactionAttemptPayload durable,
        PreparedTransactionAttempt expected)
    {
        if (durable.Summary.Sequence != expected.ExpectedPreviousAttemptCount + 1 ||
            durable.Summary.Fee != expected.Fee ||
            durable.Summary.TransactionHash != expected.Payload.TransactionHash ||
            durable.Payload.RawTransaction != expected.Payload.RawTransaction ||
            durable.UnsignedFingerprint != expected.UnsignedFingerprint)
        {
            throw new TransactionLifecycleConflictException(
                "The concurrent transaction attempt contains different signed facts.");
        }
    }

    private static void ValidateAttemptFee(
        OperationRow operation,
        TransactionFeeQuote? previous,
        TransactionFeeQuote proposed)
    {
        if (proposed.MaxFeePerGasWei > operation.MaxFee ||
            proposed.MaxPriorityFeePerGasWei > operation.MaxPriorityFee)
        {
            throw new TransactionLifecycleConflictException("The attempt exceeds durable fee caps.");
        }

        if (previous is null)
        {
            if (proposed.MaxFeePerGasWei != operation.InitialMaxFee ||
                proposed.MaxPriorityFeePerGasWei != operation.InitialPriorityFee)
            {
                throw new TransactionLifecycleConflictException(
                    "The initial signed attempt does not use the reserved fee quote.");
            }

            return;
        }

        if (!MeetsBump(previous.MaxFeePerGasWei, proposed.MaxFeePerGasWei, operation.MinimumFeeBumpBasisPoints) ||
            !MeetsBump(previous.MaxPriorityFeePerGasWei, proposed.MaxPriorityFeePerGasWei, operation.MinimumFeeBumpBasisPoints))
        {
            throw new TransactionLifecycleConflictException(
                "The replacement attempt does not satisfy the durable fee bump policy.");
        }
    }

    private static bool MeetsBump(BigInteger previous, BigInteger proposed, int basisPoints)
    {
        BigInteger numerator = previous * (10_000 + basisPoints);
        BigInteger minimum = BigInteger.DivRem(numerator, 10_000, out BigInteger remainder);
        return proposed >= (remainder.IsZero ? minimum : minimum + BigInteger.One);
    }

    private static void VerifyReceiptAgainstAttempt(
        OperationRow operation,
        TransactionAttemptPayload attempt,
        TransactionReceiptObservation receipt)
    {
        if (attempt.Summary.TransactionHash != receipt.TransactionHash ||
            receipt.GasUsed > operation.GasLimit ||
            receipt.EffectiveGasPriceWei > attempt.Summary.Fee.MaxFeePerGasWei)
        {
            throw new TransactionLifecycleConflictException(
                "The receipt does not match the selected signed attempt or its hard limits.");
        }
    }

    private static void VerifyDurableReceipt(
        OperationRow operation,
        TransactionAttemptPayload attempt,
        TransactionReceiptObservation expected)
    {
        if (operation.MinedTransactionHash != expected.TransactionHash ||
            operation.ReceiptStatus != FormatExecutionStatus(expected.Status) ||
            operation.MinedBlockNumber != expected.BlockNumber ||
            operation.MinedBlockHash != expected.BlockHash ||
            operation.ReceiptGasUsed != expected.GasUsed ||
            operation.ReceiptEffectiveGasPrice != expected.EffectiveGasPriceWei ||
            attempt.Summary.TransactionHash != expected.TransactionHash)
        {
            throw new TransactionLifecycleConflictException(
                "The durable receipt observation contains different facts.");
        }
    }

    private static TransactionLifecycleSnapshot ToSnapshot(OperationRow value)
    {
        TransactionLifecycleState state = value.ReceiptStatus switch
        {
            "succeeded" => TransactionLifecycleState.MinedSucceeded,
            "reverted" => TransactionLifecycleState.MinedReverted,
            null when value.AttemptCount == 0 => TransactionLifecycleState.Reserved,
            null when value.LatestBroadcastOutcome is null => TransactionLifecycleState.Signed,
            null when value.LatestBroadcastOutcome == "unknown" => TransactionLifecycleState.BroadcastUnknown,
            null when value.LatestBroadcastOutcome == "rejected" => TransactionLifecycleState.Rejected,
            null when value.LatestBroadcastOutcome is "accepted" or "already_known" => TransactionLifecycleState.Submitted,
            _ => throw new InvalidOperationException("Stored transaction lifecycle state is unsupported."),
        };
        return new TransactionLifecycleSnapshot(
            value.OperationId, value.PaymentId, value.ChainId, value.Signer,
            value.Router, value.Token, value.Merchant, value.Amount, value.Nonce,
            value.GasLimit, value.PolicyId, value.PolicyFingerprint, state,
            value.AttemptCount, value.BroadcastCount, value.CurrentTransactionHash,
            value.MinedTransactionHash, value.MinedBlockNumber, value.CreatedAtUtc);
    }

    private static string FormatBroadcastOutcome(TransactionBroadcastOutcomeKind value) => value switch
    {
        TransactionBroadcastOutcomeKind.Accepted => "accepted",
        TransactionBroadcastOutcomeKind.AlreadyKnown => "already_known",
        TransactionBroadcastOutcomeKind.Unknown => "unknown",
        TransactionBroadcastOutcomeKind.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static TransactionBroadcastOutcomeKind ParseBroadcastOutcome(string value) => value switch
    {
        "accepted" => TransactionBroadcastOutcomeKind.Accepted,
        "already_known" => TransactionBroadcastOutcomeKind.AlreadyKnown,
        "unknown" => TransactionBroadcastOutcomeKind.Unknown,
        "rejected" => TransactionBroadcastOutcomeKind.Rejected,
        _ => throw new InvalidOperationException($"Stored broadcast outcome '{value}' is unsupported."),
    };

    private static string FormatExecutionStatus(TransactionExecutionStatus value) => value switch
    {
        TransactionExecutionStatus.Succeeded => "succeeded",
        TransactionExecutionStatus.Reverted => "reverted",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static BigInteger ParseInteger(string value) =>
        BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static BigInteger? ReadNullableInteger(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseInteger(reader.GetString(ordinal));
    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static TransactionHash? ReadHash(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : TransactionHash.Parse(reader.GetString(ordinal));
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record OperationRow(
        TransactionOperationId OperationId,
        string RequestFingerprint,
        EvmChainId ChainId,
        EvmAddress Signer,
        EvmAddress Router,
        PaymentId PaymentId,
        EvmAddress Token,
        EvmAddress Merchant,
        RawTokenAmount Amount,
        long Nonce,
        long ObservedPendingNonce,
        long GasLimit,
        string Calldata,
        string PolicyId,
        string PolicyFingerprint,
        long MaxGasLimit,
        BigInteger InitialMaxFee,
        BigInteger InitialPriorityFee,
        BigInteger MaxFee,
        BigInteger MaxPriorityFee,
        int MinimumFeeBumpBasisPoints,
        int MaxAttempts,
        int MaxReservedNonceLead,
        DateTimeOffset CreatedAtUtc,
        int AttemptCount,
        int BroadcastCount,
        TransactionHash? CurrentTransactionHash,
        string? LatestBroadcastOutcome,
        TransactionHash? MinedTransactionHash,
        string? ReceiptStatus,
        long? MinedBlockNumber,
        TransactionHash? MinedBlockHash,
        long? ReceiptGasUsed,
        BigInteger? ReceiptEffectiveGasPrice);
}
