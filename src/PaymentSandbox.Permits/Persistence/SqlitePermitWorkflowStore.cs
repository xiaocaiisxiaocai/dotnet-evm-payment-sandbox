using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using Nethereum.Util;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Erc2612;
using PaymentSandbox.Permits.Preflight;
using PaymentSandbox.Permits.Workflow;

namespace PaymentSandbox.Permits.Persistence;

/// <summary>Durable permit nonce reservation and append-only submission history.</summary>
public sealed class SqlitePermitWorkflowStore(PermitWorkflowDatabase database)
{
    private readonly PermitWorkflowDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    internal async ValueTask<PermitWorkflowCommitResult> ReserveAsync(
        PermitReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        // deferred:false maps to BEGIN IMMEDIATE. All processes sharing this
        // file therefore serialize the read-before-insert nonce decision.
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        PermitWorkflowSnapshot? existing = await ReadByNonceAsync(
            connection,
            transaction,
            command.Draft,
            cancellationToken);
        if (existing is not null)
        {
            // A uniqueness hit is safe only when every reusable draft fact is
            // identical; otherwise the same token nonce has competing intent.
            EnsureReservationReplay(existing, command);
            await transaction.CommitAsync(cancellationToken);
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.Replayed,
                existing);
        }

        await using (SqliteCommand count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM permit_operations;";
            long current = (long)(await count.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Permit capacity count returned no value."));
            if (current >= _database.Capacity)
            {
                throw new PermitWorkflowException("The bounded permit workflow store is full.");
            }
        }

        await InsertOperationAsync(connection, transaction, command, cancellationToken);
        long transitionId = await InsertTransitionAsync(
            connection,
            transaction,
            command.OperationId,
            "reserved",
            command.Observation,
            command.CreatedAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PermitWorkflowSnapshot created = new(
            command.OperationId,
            command.Draft,
            command.Observation,
            PermitWorkflowState.Reserved,
            transitionId,
            SubmissionAuthorizationCount: 0,
            Preparation: null);
        return new PermitWorkflowCommitResult(PermitWorkflowCommitDisposition.Created, created);
    }

    public async ValueTask<PermitWorkflowSnapshot?> GetAsync(
        PermitOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, operationId, cancellationToken);
    }

    internal async ValueTask<PermitWorkflowCommitResult> PrepareAsync(
        PermitPreparationCommand command,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        PermitWorkflowSnapshot snapshot = await RequireAsync(
            connection, transaction, command.OperationId, cancellationToken);
        string calldata = CanonicalCalldata(command.Payment.Call.Data);
        string calldataHash = HashCalldata(calldata);
        if (snapshot.Preparation is not null)
        {
            bool matches = snapshot.Preparation.PaymentId == command.Payment.PaymentId &&
                snapshot.Preparation.Merchant == command.Payment.Merchant &&
                snapshot.Preparation.RequiredSender == command.Payment.RequiredSender &&
                string.Equals(snapshot.Preparation.Calldata, calldata, StringComparison.Ordinal) &&
                string.Equals(snapshot.Preparation.CalldataHash, calldataHash, StringComparison.Ordinal);
            if (!matches)
            {
                throw new PermitWorkflowException(
                    "The permit operation was already prepared with different payment facts.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.Replayed, snapshot);
        }

        RequireTransition(snapshot, command.ExpectedTransitionId, PermitWorkflowState.Reserved);
        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO permit_preparations (
                    operation_id, payment_id, merchant_address, required_sender,
                    calldata, calldata_hash, prepared_at_utc)
                VALUES ($operation, $payment, $merchant, $sender, $calldata, $hash, $time);
                """;
            insert.Parameters.AddWithValue("$operation", command.OperationId.Value);
            insert.Parameters.AddWithValue("$payment", command.Payment.PaymentId.Value);
            insert.Parameters.AddWithValue("$merchant", command.Payment.Merchant.Value);
            insert.Parameters.AddWithValue("$sender", command.Payment.RequiredSender.Value);
            insert.Parameters.AddWithValue("$calldata", calldata);
            insert.Parameters.AddWithValue("$hash", calldataHash);
            insert.Parameters.AddWithValue("$time", PermitWorkflowDatabase.Format(command.PreparedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertTransitionAsync(
            connection, transaction, command.OperationId, "prepared", null,
            command.PreparedAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PermitWorkflowCommitResult(
            PermitWorkflowCommitDisposition.Applied,
            (await GetAsync(command.OperationId, cancellationToken))!);
    }

    internal async ValueTask<(PermitWorkflowCommitResult Result, PermitSubmissionAuthorization? Authorization)>
        AuthorizeAsync(
            PermitOperationId operationId,
            long expectedTransitionId,
            PermitWorkflowState expectedState,
            VerifiedErc2612TokenSnapshot observation,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        PermitWorkflowSnapshot snapshot = await RequireAsync(
            connection, transaction, operationId, cancellationToken);
        if (snapshot.LatestTransitionId != expectedTransitionId || snapshot.State != expectedState)
        {
            await transaction.CommitAsync(cancellationToken);
            return (new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, snapshot), null);
        }

        PermitPaymentPreparation preparation = snapshot.Preparation
            ?? throw new PermitWorkflowException("The permit operation has no prepared calldata.");
        // This append commits before the method constructs/returns an object
        // containing calldata. A crash after return is therefore recoverably
        // ambiguous instead of looking like no submission was attempted.
        long transitionId = await InsertTransitionAsync(
            connection,
            transaction,
            operationId,
            "submission_unknown",
            observation,
            occurredAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PermitWorkflowSnapshot updated = (await GetAsync(operationId, cancellationToken))!;
        var authorization = new PermitSubmissionAuthorization(
            operationId,
            transitionId,
            preparation.RequiredSender,
            preparation.Calldata,
            updated.SubmissionAuthorizationCount,
            observation);
        return (new PermitWorkflowCommitResult(
            PermitWorkflowCommitDisposition.Applied, updated), authorization);
    }

    internal async ValueTask<PermitWorkflowCommitResult> RecordOutcomeAsync(
        PermitOperationId operationId,
        long authorizationTransitionId,
        PermitSubmissionOutcome outcome,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (outcome == PermitSubmissionOutcome.Unknown)
        {
            PermitWorkflowSnapshot current = await GetAsync(operationId, cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Permit operation '{operationId.Value}' was not found.");
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, current);
        }

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        PermitWorkflowSnapshot snapshot = await RequireAsync(
            connection, transaction, operationId, cancellationToken);
        string kind = outcome == PermitSubmissionOutcome.Accepted
            ? "submission_accepted"
            : "submission_rejected";
        if (await IsOutcomeReplayAsync(
            connection,
            transaction,
            operationId,
            authorizationTransitionId,
            kind,
            cancellationToken))
        {
            // The exact authorization/outcome edge is already durable. This
            // is the expected recovery path when commit succeeded but the
            // process lost the method response; never append a second edge.
            await transaction.CommitAsync(cancellationToken);
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.Replayed, snapshot);
        }

        if (snapshot.LatestTransitionId != authorizationTransitionId ||
            snapshot.State != PermitWorkflowState.SubmissionUnknown)
        {
            throw new PermitWorkflowException(
                "The submission outcome does not belong to the current authorization.");
        }

        await InsertTransitionAsync(
            connection, transaction, operationId, kind, null,
            occurredAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PermitWorkflowCommitResult(
            PermitWorkflowCommitDisposition.Applied,
            (await GetAsync(operationId, cancellationToken))!);
    }

    private static async Task<bool> IsOutcomeReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermitOperationId operationId,
        long authorizationTransitionId,
        string expectedOutcomeKind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // Read the supplied authorization and its immediate successor in this
        // operation's history. Global AUTOINCREMENT IDs need not be adjacent
        // because another operation may commit between them.
        command.CommandText =
            """
            SELECT transition_id, kind
            FROM permit_state_transitions
            WHERE operation_id = $operation
              AND transition_id >= $authorization
            ORDER BY transition_id
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$operation", operationId.Value);
        command.Parameters.AddWithValue("$authorization", authorizationTransitionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != authorizationTransitionId ||
            !string.Equals(reader.GetString(1), "submission_unknown", StringComparison.Ordinal))
        {
            return false;
        }

        return await reader.ReadAsync(cancellationToken) &&
            string.Equals(reader.GetString(1), expectedOutcomeKind, StringComparison.Ordinal);
    }

    internal async ValueTask<PermitWorkflowCommitResult> RecordTerminalAsync(
        PermitOperationId operationId,
        long expectedTransitionId,
        PermitWorkflowState terminalState,
        VerifiedErc2612TokenSnapshot? observation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        string kind = terminalState switch
        {
            PermitWorkflowState.NonceChanged => "nonce_changed",
            PermitWorkflowState.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(terminalState)),
        };
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        PermitWorkflowSnapshot snapshot = await RequireAsync(
            connection, transaction, operationId, cancellationToken);
        if (snapshot.LatestTransitionId != expectedTransitionId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, snapshot);
        }

        if (snapshot.State is PermitWorkflowState.Expired or
            PermitWorkflowState.NonceChanged or
            PermitWorkflowState.SubmissionRejected)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, snapshot);
        }

        await InsertTransitionAsync(
            connection, transaction, operationId, kind, observation,
            occurredAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PermitWorkflowCommitResult(
            PermitWorkflowCommitDisposition.Applied,
            (await GetAsync(operationId, cancellationToken))!);
    }

    private async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermitReservationCommand command,
        CancellationToken cancellationToken)
    {
        Erc2612PermitDraft draft = command.Draft;
        VerifiedErc2612TokenSnapshot observed = command.Observation;
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO permit_operations (
                operation_id, policy_fingerprint, chain_id, token_address,
                token_name, token_version, spender_address, owner_address,
                value_raw, token_nonce, issued_at_utc, deadline_utc,
                typed_data_json, domain_separator, struct_hash, digest,
                observed_block_number, observed_block_hash, runtime_code_hash,
                created_at_utc)
            VALUES (
                $operation, $policy, $chain, $token, $name, $version, $spender,
                $owner, $value, $nonce, $issued, $deadline, $json, $domain,
                $struct, $digest, $block, $blockHash, $codeHash, $created);
            """;
        insert.Parameters.AddWithValue("$operation", command.OperationId.Value);
        insert.Parameters.AddWithValue("$policy", draft.PolicyFingerprint);
        insert.Parameters.AddWithValue("$chain", Decimal(draft.ChainId.Value));
        insert.Parameters.AddWithValue("$token", draft.Token.Value);
        insert.Parameters.AddWithValue("$name", draft.TokenName);
        insert.Parameters.AddWithValue("$version", draft.TokenVersion);
        insert.Parameters.AddWithValue("$spender", draft.Spender.Value);
        insert.Parameters.AddWithValue("$owner", draft.Owner.Value);
        insert.Parameters.AddWithValue("$value", Decimal(draft.Value.Value));
        insert.Parameters.AddWithValue("$nonce", Decimal(draft.Nonce));
        insert.Parameters.AddWithValue("$issued", PermitWorkflowDatabase.Format(draft.IssuedAtUtc));
        insert.Parameters.AddWithValue("$deadline", PermitWorkflowDatabase.Format(draft.DeadlineUtc));
        insert.Parameters.AddWithValue("$json", draft.TypedDataJson);
        insert.Parameters.AddWithValue("$domain", draft.DomainSeparator);
        insert.Parameters.AddWithValue("$struct", draft.StructHash);
        insert.Parameters.AddWithValue("$digest", draft.Digest);
        insert.Parameters.AddWithValue("$block", observed.BlockNumber);
        insert.Parameters.AddWithValue("$blockHash", observed.BlockHash);
        insert.Parameters.AddWithValue("$codeHash", observed.RuntimeCodeHash);
        insert.Parameters.AddWithValue("$created", PermitWorkflowDatabase.Format(command.CreatedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermitOperationId operationId,
        string kind,
        VerifiedErc2612TokenSnapshot? observation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO permit_state_transitions (
                operation_id, kind, observed_block_number, observed_block_hash,
                observed_nonce, occurred_at_utc)
            VALUES ($operation, $kind, $block, $hash, $nonce, $time)
            RETURNING transition_id;
            """;
        insert.Parameters.AddWithValue("$operation", operationId.Value);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$block", observation is null
            ? DBNull.Value : observation.BlockNumber);
        insert.Parameters.AddWithValue("$hash", observation is null
            ? DBNull.Value : observation.BlockHash);
        insert.Parameters.AddWithValue("$nonce", observation is null
            ? DBNull.Value : Decimal(observation.Nonce));
        insert.Parameters.AddWithValue("$time", PermitWorkflowDatabase.Format(occurredAtUtc));
        return (long)(await insert.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Permit transition returned no identity."));
    }

    private static async Task<PermitWorkflowSnapshot?> ReadByNonceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Erc2612PermitDraft draft,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT operation_id FROM permit_operations
            WHERE chain_id = $chain AND token_address = $token
              AND owner_address = $owner AND token_nonce = $nonce;
            """;
        command.Parameters.AddWithValue("$chain", Decimal(draft.ChainId.Value));
        command.Parameters.AddWithValue("$token", draft.Token.Value);
        command.Parameters.AddWithValue("$owner", draft.Owner.Value);
        command.Parameters.AddWithValue("$nonce", Decimal(draft.Nonce));
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id
            ? await ReadAsync(connection, transaction, PermitOperationId.Parse(id), cancellationToken)
            : null;
    }

    private static async Task<PermitWorkflowSnapshot?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PermitOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT o.policy_fingerprint, o.chain_id, o.token_address, o.token_name,
                   o.token_version, o.spender_address, o.owner_address, o.value_raw,
                   o.token_nonce, o.issued_at_utc, o.deadline_utc, o.typed_data_json,
                   o.domain_separator, o.struct_hash, o.digest,
                   o.observed_block_number, o.observed_block_hash, o.runtime_code_hash,
                   t.transition_id, t.kind,
                   (SELECT COUNT(*) FROM permit_state_transitions a
                    WHERE a.operation_id = o.operation_id AND a.kind = 'submission_unknown'),
                   p.payment_id, p.merchant_address, p.required_sender, p.calldata,
                   p.calldata_hash, p.prepared_at_utc
            FROM permit_operations o
            JOIN permit_state_transitions t ON t.transition_id = (
                SELECT MAX(latest.transition_id) FROM permit_state_transitions latest
                WHERE latest.operation_id = o.operation_id)
            LEFT JOIN permit_preparations p ON p.operation_id = o.operation_id
            WHERE o.operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var draft = new Erc2612PermitDraft(
            reader.GetString(0),
            new EvmChainId(ParseInteger(reader.GetString(1))),
            EvmAddress.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            EvmAddress.Parse(reader.GetString(6)),
            EvmAddress.Parse(reader.GetString(5)),
            new RawTokenAmount(ParseInteger(reader.GetString(7))),
            ParseInteger(reader.GetString(8)),
            ParseTime(reader.GetString(9)),
            ParseTime(reader.GetString(10)),
            reader.GetString(11),
            Convert.FromHexString(reader.GetString(12).AsSpan(2)),
            Convert.FromHexString(reader.GetString(13).AsSpan(2)),
            Convert.FromHexString(reader.GetString(14).AsSpan(2)));
        ValidateStoredDraft(draft);
        var observation = new VerifiedErc2612TokenSnapshot(
            draft.Owner,
            draft.Nonce,
            reader.GetInt64(15),
            reader.GetString(16),
            reader.GetString(17),
            draft.DomainSeparator);
        PermitPaymentPreparation? preparation = reader.IsDBNull(21)
            ? null
            : new PermitPaymentPreparation(
                PaymentId.Parse(reader.GetString(21)),
                EvmAddress.Parse(reader.GetString(22)),
                EvmAddress.Parse(reader.GetString(23)),
                reader.GetString(24),
                reader.GetString(25),
                ParseTime(reader.GetString(26)));
        ValidateStoredPreparation(draft, preparation);
        return new PermitWorkflowSnapshot(
            operationId,
            draft,
            observation,
            ParseState(reader.GetString(19)),
            reader.GetInt64(18),
            reader.GetInt32(20),
            preparation);
    }

    /// <summary>
    /// Rebuilds the EIP-712 document instead of trusting several correlated
    /// database columns independently. This detects accidental corruption; it
    /// is deliberately not presented as authenticity against an attacker who
    /// can rewrite the whole database and its schema.
    /// </summary>
    private static void ValidateStoredDraft(Erc2612PermitDraft stored)
    {
        try
        {
            TimeSpan lifetime = stored.DeadlineUtc - stored.IssuedAtUtc;
            var policy = new Erc2612PermitPolicy(
                stored.ChainId,
                stored.Token,
                stored.TokenName,
                stored.TokenVersion,
                stored.Spender,
                lifetime);
            var service = new Erc2612PermitService(
                policy,
                new StoredDraftTimeProvider(stored.IssuedAtUtc));
            Erc2612PermitDraft rebuilt = service.CreateDraft(
                stored.Owner,
                stored.Value,
                stored.Nonce);
            bool matches = stored.PolicyFingerprint == rebuilt.PolicyFingerprint &&
                stored.IssuedAtUtc == rebuilt.IssuedAtUtc &&
                stored.DeadlineUtc == rebuilt.DeadlineUtc &&
                stored.TypedDataJson == rebuilt.TypedDataJson &&
                stored.DomainSeparator == rebuilt.DomainSeparator &&
                stored.StructHash == rebuilt.StructHash &&
                stored.Digest == rebuilt.Digest;
            if (!matches)
            {
                throw CorruptDatabase();
            }
        }
        catch (PermitWorkflowException)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not include typed data or other stored attacker-controlled
            // values in a diagnostic that may later be logged.
            throw CorruptDatabase();
        }
    }

    /// <summary>
    /// Checks the stored calldata hash and the ABI words that duplicate local
    /// payment facts. The signature words remain opaque here; they were checked
    /// before the immutable preparation row was originally written.
    /// </summary>
    private static void ValidateStoredPreparation(
        Erc2612PermitDraft draft,
        PermitPaymentPreparation? preparation)
    {
        if (preparation is null)
        {
            return;
        }

        try
        {
            string calldata = CanonicalCalldata(preparation.Calldata);
            string paymentId = ReadCalldataWord(calldata, 0);
            string token = ReadCalldataWord(calldata, 1);
            string merchant = ReadCalldataWord(calldata, 2);
            BigInteger value = ParseHexWord(ReadCalldataWord(calldata, 3));
            BigInteger deadline = ParseHexWord(ReadCalldataWord(calldata, 4));
            BigInteger v = ParseHexWord(ReadCalldataWord(calldata, 5));
            BigInteger r = ParseHexWord(ReadCalldataWord(calldata, 6));
            BigInteger s = ParseHexWord(ReadCalldataWord(calldata, 7));
            bool matches = preparation.Calldata == calldata &&
                preparation.CalldataHash == HashCalldata(calldata) &&
                preparation.RequiredSender == draft.Owner &&
                paymentId == preparation.PaymentId.Value[2..] &&
                IsAddressWord(token, draft.Token) &&
                IsAddressWord(merchant, preparation.Merchant) &&
                value == draft.Value.Value &&
                deadline == draft.DeadlineUnixSeconds &&
                (v == 27 || v == 28) && !r.IsZero && !s.IsZero;
            if (!matches)
            {
                throw CorruptDatabase();
            }
        }
        catch (PermitWorkflowException)
        {
            throw;
        }
        catch (Exception)
        {
            throw CorruptDatabase();
        }
    }

    private static string ReadCalldataWord(string calldata, int index) =>
        calldata.Substring(10 + (index * 64), 64);

    private static BigInteger ParseHexWord(string value) =>
        BigInteger.Parse(
            "0" + value,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);

    private static bool IsAddressWord(string word, EvmAddress address) =>
        word.AsSpan(0, 24).IndexOfAnyExcept('0') < 0 &&
        string.Equals(word[24..], address.Value[2..], StringComparison.Ordinal);

    private static PermitWorkflowException CorruptDatabase() =>
        new("The permit workflow database contains inconsistent protected facts.");

    private static async Task<PermitWorkflowSnapshot> RequireAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermitOperationId operationId,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, operationId, cancellationToken)
        ?? throw new KeyNotFoundException(
            $"Permit operation '{operationId.Value}' was not found.");

    private static void EnsureReservationReplay(
        PermitWorkflowSnapshot existing,
        PermitReservationCommand command)
    {
        Erc2612PermitDraft left = existing.Draft;
        Erc2612PermitDraft right = command.Draft;
        bool matches = left.PolicyFingerprint == right.PolicyFingerprint &&
            left.Value == right.Value && left.Digest == right.Digest &&
            left.TypedDataJson == right.TypedDataJson &&
            existing.InitialObservation.Owner == command.Observation.Owner &&
            existing.InitialObservation.Nonce == command.Observation.Nonce &&
            existing.InitialObservation.RuntimeCodeHash == command.Observation.RuntimeCodeHash &&
            existing.InitialObservation.DomainSeparator == command.Observation.DomainSeparator;
        if (!matches)
        {
            throw new PermitWorkflowException(
                "The observed token nonce is already reserved for different permit facts.");
        }
    }

    private static void RequireTransition(
        PermitWorkflowSnapshot snapshot,
        long expectedTransitionId,
        PermitWorkflowState expectedState)
    {
        if (snapshot.LatestTransitionId != expectedTransitionId || snapshot.State != expectedState)
        {
            throw new PermitWorkflowException(
                "The permit workflow changed before this operation could commit.");
        }
    }

    private static string CanonicalCalldata(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string canonical = value.ToLowerInvariant();
        if (canonical.Length != 522 || !canonical.StartsWith("0x1f2b568e", StringComparison.Ordinal) ||
            !canonical.AsSpan(2).ToString().All(Uri.IsHexDigit))
        {
            throw new PermitWorkflowException("Prepared permit calldata is not canonical.");
        }

        return canonical;
    }

    private static string HashCalldata(string calldata) =>
        $"0x{Convert.ToHexStringLower(Sha3Keccack.Current.CalculateHash(
            Convert.FromHexString(calldata.AsSpan(2))))}";

    private static PermitWorkflowState ParseState(string value) => value switch
    {
        "reserved" => PermitWorkflowState.Reserved,
        "prepared" => PermitWorkflowState.Prepared,
        "submission_unknown" => PermitWorkflowState.SubmissionUnknown,
        "submission_accepted" => PermitWorkflowState.SubmissionAccepted,
        "submission_rejected" => PermitWorkflowState.SubmissionRejected,
        "nonce_changed" => PermitWorkflowState.NonceChanged,
        "expired" => PermitWorkflowState.Expired,
        _ => throw new InvalidOperationException("The permit database contains an unknown state."),
    };

    private static BigInteger ParseInteger(string value) =>
        BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string Decimal(BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private sealed class StoredDraftTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
