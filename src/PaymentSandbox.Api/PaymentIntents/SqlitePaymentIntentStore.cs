using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using PaymentSandbox.Api.Persistence;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

/// <summary>A durable SQLite implementation of atomic intent idempotency.</summary>
/// <remarks>
/// Creation is insert-first: SQLite arbitrates the unique idempotency key before
/// this code reads an existing row. The insert and possible conflict read share
/// one transaction, avoiding the classic SELECT-then-INSERT race across API
/// processes that point at the same database file.
/// </remarks>
public sealed class SqlitePaymentIntentStore(PaymentIntentDatabase database)
    : IPaymentIntentStore
{
    private readonly PaymentIntentDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<PaymentIntentCreateResult> CreateOrGetAsync(
        IdempotencyKey idempotencyKey,
        PaymentIntent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        int inserted = await TryInsertAsync(
            connection,
            transaction,
            idempotencyKey,
            candidate,
            cancellationToken);

        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PaymentIntentCreateResult(PaymentIntentCreateDisposition.Created, candidate);
        }

        PaymentIntent existing = await FindByIdempotencyKeyAsync(
            connection,
            transaction,
            idempotencyKey,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "SQLite reported an idempotency conflict but returned no existing row.");

        await transaction.CommitAsync(cancellationToken);

        return existing.Terms == candidate.Terms
            ? new PaymentIntentCreateResult(PaymentIntentCreateDisposition.Replayed, existing)
            // Do not return the stored resource when the same key names different terms.
            : new PaymentIntentCreateResult(PaymentIntentCreateDisposition.Conflict, Intent: null);
    }

    public async ValueTask<PaymentIntent?> FindByIdAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentId);
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payment_id, chain_id, token_address, merchant_address,
                   amount_raw, status, created_at_utc
            FROM payment_intents
            WHERE payment_id = $paymentId;
            """;
        command.Parameters.AddWithValue("$paymentId", paymentId.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    private static async Task<int> TryInsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdempotencyKey idempotencyKey,
        PaymentIntent candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO payment_intents (
                payment_id, idempotency_key, chain_id, token_address,
                merchant_address, amount_raw, status, created_at_utc)
            VALUES (
                $paymentId, $idempotencyKey, $chainId, $tokenAddress,
                $merchantAddress, $amountRaw, 'created', $createdAtUtc)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$paymentId", candidate.Id.Value);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey.Value);
        command.Parameters.AddWithValue("$chainId", candidate.Terms.ChainId.ToString());
        command.Parameters.AddWithValue("$tokenAddress", candidate.Terms.Token.Value);
        command.Parameters.AddWithValue("$merchantAddress", candidate.Terms.Merchant.Value);
        command.Parameters.AddWithValue("$amountRaw", candidate.Terms.Amount.ToString());
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            candidate.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PaymentIntent?> FindByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT payment_id, chain_id, token_address, merchant_address,
                   amount_raw, status, created_at_utc
            FROM payment_intents
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    private static PaymentIntent ReadIntent(SqliteDataReader reader)
    {
        string status = reader.GetString(5);
        if (!string.Equals(status, "created", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported stored intent status '{status}'.");
        }

        var terms = new PaymentIntentTerms(
            EvmChainId.Parse(reader.GetString(1)),
            EvmAddress.Parse(reader.GetString(2)),
            EvmAddress.Parse(reader.GetString(3)),
            new RawTokenAmount(BigInteger.Parse(
                reader.GetString(4),
                NumberStyles.None,
                CultureInfo.InvariantCulture)));
        DateTimeOffset createdAtUtc = DateTimeOffset.ParseExact(
            reader.GetString(6),
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return PaymentIntent.Create(PaymentId.Parse(reader.GetString(0)), terms, createdAtUtc);
    }
}
