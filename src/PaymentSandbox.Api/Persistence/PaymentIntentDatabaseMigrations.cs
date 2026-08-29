namespace PaymentSandbox.Api.Persistence;

/// <summary>The ordered, append-only schema history owned by this application.</summary>
internal static class PaymentIntentDatabaseMigrations
{
    internal static readonly PaymentIntentDatabaseMigration[] All =
    [
        new(
            Version: 1,
            Name: "create_payment_intents",
            Sql:
            """
            CREATE TABLE payment_intents (
                payment_id TEXT NOT NULL PRIMARY KEY
                    CHECK (
                        length(payment_id) = 66
                        AND substr(payment_id, 1, 2) = '0x'
                        AND payment_id = lower(payment_id)
                        AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'
                    ),
                idempotency_key TEXT NOT NULL COLLATE BINARY
                    UNIQUE
                    CHECK (length(idempotency_key) BETWEEN 1 AND 128),
                chain_id TEXT NOT NULL
                    CHECK (
                        length(chain_id) > 0
                        AND chain_id NOT GLOB '*[^0-9]*'
                        AND chain_id <> '0'
                        AND substr(chain_id, 1, 1) <> '0'
                    ),
                token_address TEXT NOT NULL
                    CHECK (
                        length(token_address) = 42
                        AND substr(token_address, 1, 2) = '0x'
                        AND token_address = lower(token_address)
                        AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'
                    ),
                merchant_address TEXT NOT NULL
                    CHECK (
                        length(merchant_address) = 42
                        AND substr(merchant_address, 1, 2) = '0x'
                        AND merchant_address = lower(merchant_address)
                        AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'
                    ),
                amount_raw TEXT NOT NULL
                    CHECK (
                        length(amount_raw) > 0
                        AND amount_raw NOT GLOB '*[^0-9]*'
                        AND amount_raw <> '0'
                        AND substr(amount_raw, 1, 1) <> '0'
                    ),
                status TEXT NOT NULL CHECK (status = 'created'),
                created_at_utc TEXT NOT NULL
            ) STRICT;
            """),
    ];
}

internal sealed record PaymentIntentDatabaseMigration(
    long Version,
    string Name,
    string Sql);
