namespace PaymentSandbox.Indexer.Persistence;

/// <summary>The ordered, append-only schema history owned by the indexer.</summary>
internal static class IndexerDatabaseMigrations
{
    internal static readonly IndexerDatabaseMigration[] All =
    [
        new(
            Version: 1,
            Name: "create_chain_observations",
            Sql:
            """
            CREATE TABLE indexer_checkpoints (
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                start_block_number INTEGER NOT NULL CHECK (start_block_number >= 0),
                last_block_number INTEGER NOT NULL CHECK (last_block_number >= start_block_number),
                last_block_hash TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0),
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (chain_id, router_address),
                CHECK (
                    length(chain_id) > 0
                    AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0'
                    AND substr(chain_id, 1, 1) <> '0'
                ),
                CHECK (
                    length(router_address) = 42
                    AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND router_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(last_block_hash) = 66
                    AND substr(last_block_hash, 1, 2) = '0x'
                    AND last_block_hash = lower(last_block_hash)
                    AND substr(last_block_hash, 3) NOT GLOB '*[^0-9a-f]*'
                )
            ) STRICT;

            CREATE TABLE observed_blocks (
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                block_number INTEGER NOT NULL CHECK (block_number >= 0),
                block_hash TEXT NOT NULL,
                parent_hash TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                PRIMARY KEY (chain_id, router_address, block_number, block_hash),
                CHECK (
                    length(chain_id) > 0
                    AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0'
                    AND substr(chain_id, 1, 1) <> '0'
                ),
                CHECK (
                    length(router_address) = 42
                    AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND router_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(block_hash) = 66
                    AND substr(block_hash, 1, 2) = '0x'
                    AND block_hash = lower(block_hash)
                    AND substr(block_hash, 3) NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(parent_hash) = 66
                    AND substr(parent_hash, 1, 2) = '0x'
                    AND parent_hash = lower(parent_hash)
                    AND substr(parent_hash, 3) NOT GLOB '*[^0-9a-f]*'
                )
            ) STRICT;

            CREATE TABLE payment_recorded_observations (
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                block_number INTEGER NOT NULL CHECK (block_number >= 0),
                block_hash TEXT NOT NULL,
                transaction_hash TEXT NOT NULL,
                log_index INTEGER NOT NULL CHECK (log_index >= 0),
                payment_id TEXT NOT NULL,
                payer_address TEXT NOT NULL,
                token_address TEXT NOT NULL,
                merchant_address TEXT NOT NULL,
                amount_raw TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                PRIMARY KEY (
                    chain_id, router_address, block_hash,
                    transaction_hash, log_index
                ),
                FOREIGN KEY (chain_id, router_address, block_number, block_hash)
                    REFERENCES observed_blocks (
                        chain_id, router_address, block_number, block_hash
                    ),
                CHECK (
                    length(chain_id) > 0
                    AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0'
                    AND substr(chain_id, 1, 1) <> '0'
                ),
                CHECK (
                    length(router_address) = 42
                    AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND router_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(block_hash) = 66
                    AND substr(block_hash, 1, 2) = '0x'
                    AND block_hash = lower(block_hash)
                    AND substr(block_hash, 3) NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(transaction_hash) = 66
                    AND substr(transaction_hash, 1, 2) = '0x'
                    AND transaction_hash = lower(transaction_hash)
                    AND substr(transaction_hash, 3) NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(payment_id) = 66
                    AND substr(payment_id, 1, 2) = '0x'
                    AND payment_id = lower(payment_id)
                    AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'
                    AND payment_id <> '0x0000000000000000000000000000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(payer_address) = 42
                    AND substr(payer_address, 1, 2) = '0x'
                    AND payer_address = lower(payer_address)
                    AND substr(payer_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND payer_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(token_address) = 42
                    AND substr(token_address, 1, 2) = '0x'
                    AND token_address = lower(token_address)
                    AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND token_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(merchant_address) = 42
                    AND substr(merchant_address, 1, 2) = '0x'
                    AND merchant_address = lower(merchant_address)
                    AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND merchant_address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (
                    length(amount_raw) > 0
                    AND amount_raw NOT GLOB '*[^0-9]*'
                    AND amount_raw <> '0'
                    AND substr(amount_raw, 1, 1) <> '0'
                    AND length(amount_raw) <= 78
                    AND (
                        length(amount_raw) < 78
                        OR amount_raw <= '115792089237316195423570985008687907853269984665640564039457584007913129639935'
                    )
                )
            ) STRICT;

            CREATE INDEX ix_payment_observations_payment_id
                ON payment_recorded_observations (
                    chain_id, router_address, payment_id, block_number
                );
            """),
    ];
}

internal sealed record IndexerDatabaseMigration(long Version, string Name, string Sql);
