using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Authentication.Persistence;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>SQLite browser-flow binding, opaque session, and revocation store.</summary>
public sealed class SqliteSiweBrowserSessionStore : ISiweBrowserSessionStore
{
    private readonly SiweChallengeDatabase _database;

    public SqliteSiweBrowserSessionStore(SiweChallengeDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<SiweFlowBindResult> TryBindFlowAsync(
        string nonce,
        string bindingTokenHash,
        DateTimeOffset expirationTimeUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(nonce, bindingTokenHash);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteTransaction transaction = BeginImmediate(connection, cancellationToken);
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO siwe_login_flows (
                nonce, binding_token_hash, expiration_at_unix_seconds,
                consumed_at_unix_milliseconds)
            SELECT nonce, $bindingTokenHash, expiration_at_unix_seconds, NULL
            FROM siwe_challenges
            WHERE nonce = $nonce
              AND expiration_at_unix_seconds = $expirationAtUnixSeconds
            ON CONFLICT DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$nonce", nonce);
        insert.Parameters.AddWithValue("$bindingTokenHash", bindingTokenHash);
        insert.Parameters.AddWithValue(
            "$expirationAtUnixSeconds", expirationTimeUtc.ToUniversalTime().ToUnixTimeSeconds());
        int inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return SiweFlowBindResult.Bound;
        }

        SiweFlowBindResult result;
        if (await ExistsAsync(
                connection, transaction, "siwe_login_flows", "nonce", nonce, cancellationToken))
        {
            result = SiweFlowBindResult.NonceAlreadyBound;
        }
        else if (await ExistsAsync(
                connection,
                transaction,
                "siwe_login_flows",
                "binding_token_hash",
                bindingTokenHash,
                cancellationToken))
        {
            result = SiweFlowBindResult.DuplicateBindingToken;
        }
        else if (!await ExistsAsync(
                connection, transaction, "siwe_challenges", "nonce", nonce, cancellationToken))
        {
            result = SiweFlowBindResult.ChallengeNotFound;
        }
        else
        {
            throw new InvalidOperationException(
                "The browser flow expiration differs from its issued challenge.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SiweFlowValidationResult> ValidateFlowAsync(
        string nonce,
        string bindingTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(nonce, bindingTokenHash);
        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT binding_token_hash, expiration_at_unix_seconds,
                   consumed_at_unix_milliseconds
            FROM siwe_login_flows
            WHERE nonce = $nonce;
            """;
        command.Parameters.AddWithValue("$nonce", nonce);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadFlowValidationAsync(
            reader, bindingTokenHash, observedAtUtc, cancellationToken);
    }

    public async Task<SiweSessionCreateResult> TryCreateSessionAsync(
        string nonce,
        string bindingTokenHash,
        string sessionTokenHash,
        string csrfTokenHash,
        EvmAddress address,
        EvmChainId chainId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expirationTimeUtc,
        string? previousSessionTokenHash,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(nonce, bindingTokenHash, sessionTokenHash, csrfTokenHash);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(chainId);
        createdAtUtc = createdAtUtc.ToUniversalTime();
        expirationTimeUtc = expirationTimeUtc.ToUniversalTime();

        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteTransaction transaction = BeginImmediate(connection, cancellationToken);
        SiweFlowValidationResult flow = await ReadFlowAsync(
            connection,
            transaction,
            nonce,
            bindingTokenHash,
            createdAtUtc,
            cancellationToken);
        if (flow != SiweFlowValidationResult.Valid)
        {
            await transaction.CommitAsync(cancellationToken);
            return MapFlowCreateResult(flow);
        }

        long createdAtMilliseconds = createdAtUtc.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(previousSessionTokenHash) &&
            !string.Equals(previousSessionTokenHash, sessionTokenHash, StringComparison.Ordinal))
        {
            // Login proof is stronger than possession of the old session. Revoke
            // it inside the same transaction that creates the replacement.
            await using SqliteCommand rotate = connection.CreateCommand();
            rotate.Transaction = transaction;
            rotate.CommandText =
                """
                UPDATE siwe_sessions
                SET revoked_at_unix_milliseconds = $revokedAt
                WHERE session_token_hash = $previousSessionTokenHash
                  AND revoked_at_unix_milliseconds IS NULL;
                """;
            rotate.Parameters.AddWithValue("$revokedAt", createdAtMilliseconds);
            rotate.Parameters.AddWithValue(
                "$previousSessionTokenHash", previousSessionTokenHash);
            await rotate.ExecuteNonQueryAsync(cancellationToken);
        }

        long count = await CountSessionsAsync(connection, transaction, cancellationToken);
        if (count >= _database.SessionCapacity)
        {
            await using SqliteCommand cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM siwe_sessions
                WHERE revoked_at_unix_milliseconds IS NOT NULL
                   OR expiration_at_unix_seconds <= $createdAtUnixSeconds;
                """;
            cleanup.Parameters.AddWithValue(
                "$createdAtUnixSeconds", createdAtUtc.ToUnixTimeSeconds());
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
            count = await CountSessionsAsync(connection, transaction, cancellationToken);
        }

        if (count >= _database.SessionCapacity)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SiweSessionCreateResult.CapacityExceeded;
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO siwe_sessions (
                session_token_hash, csrf_token_hash, address, chain_id,
                created_at_unix_seconds, expiration_at_unix_seconds,
                revoked_at_unix_milliseconds)
            VALUES (
                $sessionTokenHash, $csrfTokenHash, $address, $chainId,
                $createdAtUnixSeconds, $expirationAtUnixSeconds, NULL)
            ON CONFLICT DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$sessionTokenHash", sessionTokenHash);
        insert.Parameters.AddWithValue("$csrfTokenHash", csrfTokenHash);
        insert.Parameters.AddWithValue("$address", address.Value);
        insert.Parameters.AddWithValue(
            "$chainId", chainId.Value.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue(
            "$createdAtUnixSeconds", createdAtUtc.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue(
            "$expirationAtUnixSeconds", expirationTimeUtc.ToUnixTimeSeconds());
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            // Roll back a tentative old-session revocation before retrying with
            // new random credentials.
            await transaction.RollbackAsync(cancellationToken);
            return SiweSessionCreateResult.DuplicateSessionToken;
        }

        await using SqliteCommand consumeFlow = connection.CreateCommand();
        consumeFlow.Transaction = transaction;
        consumeFlow.CommandText =
            """
            UPDATE siwe_login_flows
            SET consumed_at_unix_milliseconds = $consumedAt
            WHERE nonce = $nonce
              AND binding_token_hash = $bindingTokenHash
              AND consumed_at_unix_milliseconds IS NULL
              AND $consumedAt < expiration_at_unix_seconds * 1000;
            """;
        consumeFlow.Parameters.AddWithValue("$consumedAt", createdAtMilliseconds);
        consumeFlow.Parameters.AddWithValue("$nonce", nonce);
        consumeFlow.Parameters.AddWithValue("$bindingTokenHash", bindingTokenHash);
        if (await consumeFlow.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "A validated SIWE browser flow could not be consumed.");
        }

        await transaction.CommitAsync(cancellationToken);
        return SiweSessionCreateResult.Created;
    }

    public async Task<SiweSessionLookup> FindSessionAsync(
        string sessionTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(sessionTokenHash);
        observedAtUtc = observedAtUtc.ToUniversalTime();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT address, chain_id, created_at_unix_seconds,
                   expiration_at_unix_seconds, revoked_at_unix_milliseconds
            FROM siwe_sessions
            WHERE session_token_hash = $sessionTokenHash;
            """;
        command.Parameters.AddWithValue("$sessionTokenHash", sessionTokenHash);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SiweSessionLookup(SiweSessionLookupResult.NotFound, null);
        }

        if (!reader.IsDBNull(4))
        {
            return new SiweSessionLookup(SiweSessionLookupResult.Revoked, null);
        }

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3));
        if (observedAtUtc >= expiresAt)
        {
            return new SiweSessionLookup(SiweSessionLookupResult.Expired, null);
        }

        var session = new SiweBrowserSession(
            EvmAddress.Parse(reader.GetString(0)),
            EvmChainId.Parse(reader.GetString(1)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            expiresAt);
        return new SiweSessionLookup(SiweSessionLookupResult.Active, session);
    }

    public async Task<SiweSessionRevokeResult> TryRevokeSessionAsync(
        string sessionTokenHash,
        string csrfTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(sessionTokenHash, csrfTokenHash);
        observedAtUtc = observedAtUtc.ToUniversalTime();
        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteTransaction transaction = BeginImmediate(connection, cancellationToken);
        await using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            """
            SELECT csrf_token_hash, expiration_at_unix_seconds,
                   revoked_at_unix_milliseconds
            FROM siwe_sessions
            WHERE session_token_hash = $sessionTokenHash;
            """;
        read.Parameters.AddWithValue("$sessionTokenHash", sessionTokenHash);
        await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
        SiweSessionRevokeResult result;
        if (!await reader.ReadAsync(cancellationToken))
        {
            result = SiweSessionRevokeResult.NotFound;
        }
        else if (!reader.IsDBNull(2))
        {
            result = SiweSessionRevokeResult.AlreadyRevoked;
        }
        else if (observedAtUtc >= DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)))
        {
            result = SiweSessionRevokeResult.Expired;
        }
        else if (!string.Equals(reader.GetString(0), csrfTokenHash, StringComparison.Ordinal))
        {
            result = SiweSessionRevokeResult.CsrfMismatch;
        }
        else
        {
            result = SiweSessionRevokeResult.Revoked;
        }

        await reader.DisposeAsync();
        if (result == SiweSessionRevokeResult.Revoked)
        {
            await using SqliteCommand revoke = connection.CreateCommand();
            revoke.Transaction = transaction;
            revoke.CommandText =
                """
                UPDATE siwe_sessions
                SET revoked_at_unix_milliseconds = $revokedAt
                WHERE session_token_hash = $sessionTokenHash
                  AND revoked_at_unix_milliseconds IS NULL;
                """;
            revoke.Parameters.AddWithValue(
                "$revokedAt", observedAtUtc.ToUnixTimeMilliseconds());
            revoke.Parameters.AddWithValue("$sessionTokenHash", sessionTokenHash);
            if (await revoke.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("An active SIWE session could not be revoked.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<SiweFlowValidationResult> ReadFlowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nonce,
        string bindingTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT binding_token_hash, expiration_at_unix_seconds,
                   consumed_at_unix_milliseconds
            FROM siwe_login_flows
            WHERE nonce = $nonce;
            """;
        command.Parameters.AddWithValue("$nonce", nonce);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadFlowValidationAsync(
            reader, bindingTokenHash, observedAtUtc, cancellationToken);
    }

    private static async Task<SiweFlowValidationResult> ReadFlowValidationAsync(
        SqliteDataReader reader,
        string bindingTokenHash,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return SiweFlowValidationResult.NotFound;
        }

        if (!string.Equals(reader.GetString(0), bindingTokenHash, StringComparison.Ordinal))
        {
            return SiweFlowValidationResult.BindingMismatch;
        }

        if (!reader.IsDBNull(2))
        {
            return SiweFlowValidationResult.AlreadyConsumed;
        }

        return observedAtUtc.ToUniversalTime() >=
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))
                ? SiweFlowValidationResult.Expired
                : SiweFlowValidationResult.Valid;
    }

    private static SiweSessionCreateResult MapFlowCreateResult(
        SiweFlowValidationResult result) => result switch
        {
            SiweFlowValidationResult.NotFound => SiweSessionCreateResult.FlowNotFound,
            SiweFlowValidationResult.BindingMismatch =>
                SiweSessionCreateResult.FlowBindingMismatch,
            SiweFlowValidationResult.Expired => SiweSessionCreateResult.FlowExpired,
            SiweFlowValidationResult.AlreadyConsumed =>
                SiweSessionCreateResult.FlowAlreadyConsumed,
            _ => throw new InvalidOperationException("Unexpected valid browser flow mapping."),
        };

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // Table and column are private constants selected only by this class.
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {column} = $value);";
        command.Parameters.AddWithValue("$value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    private static async Task<long> CountSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM siwe_sessions;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static SqliteTransaction BeginImmediate(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
    }

    private static void ValidateRequired(params string[] values)
    {
        foreach (string value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        }
    }
}
