using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Authentication.Persistence;

namespace PaymentSandbox.Authentication.Siwe;

/// <summary>Durable, cross-connection one-time storage for SIWE challenges.</summary>
/// <remarks>
/// Every mutation starts an immediate SQLite transaction. Acquiring the writer
/// reservation before reading prevents two processes from both observing an
/// unused nonce and then racing to consume it. The database file is local and
/// mutable; it is durable coordination state, not an identity trust anchor.
/// </remarks>
public sealed class SqliteSiweChallengeStore : ISiweChallengeStore
{
    private readonly SiweChallengeDatabase _database;

    public SqliteSiweChallengeStore(SiweChallengeDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<SiweChallengeAddResult> TryAddAsync(
        SiweChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteTransaction transaction = BeginImmediate(connection, cancellationToken);

        // Preserve collision semantics even when an old row would otherwise be
        // eligible for cleanup. Reusing a nonce is never treated as fresh issue.
        if (await NonceExistsAsync(connection, transaction, challenge.Nonce, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return SiweChallengeAddResult.DuplicateNonce;
        }

        long count = await CountAsync(connection, transaction, cancellationToken);
        if (count >= _database.Capacity)
        {
            // Cleanup and the following capacity decision share the writer lock.
            // Another process cannot insert between them and overfill the store.
            await using SqliteCommand cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM siwe_challenges
                WHERE consumed_at_unix_milliseconds IS NOT NULL
                   OR expiration_at_unix_seconds <= $issuedAtUnixSeconds;
                """;
            cleanup.Parameters.AddWithValue(
                "$issuedAtUnixSeconds",
                challenge.IssuedAtUtc.ToUnixTimeSeconds());
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
            count = await CountAsync(connection, transaction, cancellationToken);
        }

        if (count >= _database.Capacity)
        {
            await transaction.CommitAsync(cancellationToken);
            return SiweChallengeAddResult.CapacityExceeded;
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO siwe_challenges (
                nonce, domain, request_uri, chain_id, statement,
                issued_at_unix_seconds, expiration_at_unix_seconds,
                policy_fingerprint, consumed_at_unix_milliseconds)
            VALUES (
                $nonce, $domain, $requestUri, $chainId, $statement,
                $issuedAtUnixSeconds, $expirationAtUnixSeconds,
                $policyFingerprint, NULL);
            """;
        AddChallengeParameters(insert, challenge);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SiweChallengeAddResult.Added;
    }

    public async Task<SiweChallengeConsumeResult> TryConsumeAsync(
        SiweMessage message,
        string policyFingerprint,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFingerprint);
        cancellationToken.ThrowIfCancellationRequested();
        observedAtUtc = observedAtUtc.ToUniversalTime();
        long observedAtUnixMilliseconds = observedAtUtc.ToUnixTimeMilliseconds();

        await using SqliteConnection connection = await _database.OpenConnectionAsync(
            cancellationToken);
        await using SqliteTransaction transaction = BeginImmediate(connection, cancellationToken);

        // This is the state transition. All signed facts participate in the
        // predicate, and expiration is exclusive: observed time must be before
        // the stored whole-second deadline. A successful update can happen once.
        await using SqliteCommand consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText =
            """
            UPDATE siwe_challenges
            SET consumed_at_unix_milliseconds = $observedAtUnixMilliseconds
            WHERE nonce = $nonce
              AND consumed_at_unix_milliseconds IS NULL
              AND $observedAtUnixMilliseconds < expiration_at_unix_seconds * 1000
              AND domain = $domain
              AND request_uri = $requestUri
              AND chain_id = $chainId
              AND statement = $statement
              AND issued_at_unix_seconds = $issuedAtUnixSeconds
              AND expiration_at_unix_seconds = $expirationAtUnixSeconds
              AND policy_fingerprint = $policyFingerprint;
            """;
        AddMessageParameters(consume, message, policyFingerprint);
        consume.Parameters.AddWithValue(
            "$observedAtUnixMilliseconds",
            observedAtUnixMilliseconds);
        int changed = await consume.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return SiweChallengeConsumeResult.Consumed;
        }

        // The conditional update deliberately reveals no attacker-controlled
        // values. This read only classifies why zero rows changed so the service
        // can return a stable, non-sensitive failure code.
        await using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            """
            SELECT nonce, domain, request_uri, chain_id, statement,
                   issued_at_unix_seconds, expiration_at_unix_seconds,
                   policy_fingerprint, consumed_at_unix_milliseconds
            FROM siwe_challenges
            WHERE nonce = $nonce;
            """;
        read.Parameters.AddWithValue("$nonce", message.Nonce);
        await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
        SiweChallengeConsumeResult result;
        if (!await reader.ReadAsync(cancellationToken))
        {
            result = SiweChallengeConsumeResult.NotFound;
        }
        else if (!reader.IsDBNull(8))
        {
            result = SiweChallengeConsumeResult.AlreadyConsumed;
        }
        else if (observedAtUtc >= DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)))
        {
            result = SiweChallengeConsumeResult.Expired;
        }
        else
        {
            result = RowMatches(reader, message, policyFingerprint)
                ? throw new InvalidOperationException(
                    "SQLite rejected a valid unconsumed SIWE challenge transition.")
                : SiweChallengeConsumeResult.FactsMismatch;
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static SqliteTransaction BeginImmediate(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
    }

    private static async Task<bool> NonceExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nonce,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM siwe_challenges WHERE nonce = $nonce);";
        command.Parameters.AddWithValue("$nonce", nonce);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM siwe_challenges;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void AddChallengeParameters(
        SqliteCommand command,
        SiweChallenge challenge)
    {
        command.Parameters.AddWithValue("$nonce", challenge.Nonce);
        command.Parameters.AddWithValue("$domain", challenge.Domain);
        command.Parameters.AddWithValue("$requestUri", challenge.RequestUri.AbsoluteUri);
        command.Parameters.AddWithValue(
            "$chainId",
            challenge.ChainId.Value.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$statement", challenge.Statement);
        command.Parameters.AddWithValue(
            "$issuedAtUnixSeconds",
            challenge.IssuedAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(
            "$expirationAtUnixSeconds",
            challenge.ExpirationTimeUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(
            "$policyFingerprint",
            challenge.PolicyFingerprint);
    }

    private static void AddMessageParameters(
        SqliteCommand command,
        SiweMessage message,
        string policyFingerprint)
    {
        command.Parameters.AddWithValue("$nonce", message.Nonce);
        command.Parameters.AddWithValue("$domain", message.Domain);
        command.Parameters.AddWithValue("$requestUri", message.RequestUri.AbsoluteUri);
        command.Parameters.AddWithValue(
            "$chainId",
            message.ChainId.Value.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$statement", message.Statement);
        command.Parameters.AddWithValue(
            "$issuedAtUnixSeconds",
            message.IssuedAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(
            "$expirationAtUnixSeconds",
            message.ExpirationTimeUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$policyFingerprint", policyFingerprint);
    }

    private static bool RowMatches(
        SqliteDataReader reader,
        SiweMessage message,
        string policyFingerprint) =>
        string.Equals(reader.GetString(0), message.Nonce, StringComparison.Ordinal) &&
        string.Equals(reader.GetString(1), message.Domain, StringComparison.Ordinal) &&
        string.Equals(reader.GetString(2), message.RequestUri.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(
            reader.GetString(3),
            message.ChainId.Value.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal) &&
        string.Equals(reader.GetString(4), message.Statement, StringComparison.Ordinal) &&
        reader.GetInt64(5) == message.IssuedAtUtc.ToUnixTimeSeconds() &&
        reader.GetInt64(6) == message.ExpirationTimeUtc.ToUnixTimeSeconds() &&
        string.Equals(reader.GetString(7), policyFingerprint, StringComparison.Ordinal);
}
