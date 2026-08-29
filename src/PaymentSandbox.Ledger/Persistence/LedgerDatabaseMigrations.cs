namespace PaymentSandbox.Ledger.Persistence;

/// <summary>The ordered schema history owned exclusively by the ledger database.</summary>
internal static class LedgerDatabaseMigrations
{
    internal static readonly LedgerDatabaseMigration[] All =
    [
        new(
            Version: 1,
            Name: "create_canonical_payment_ledger",
            Sql:
            """
            CREATE TABLE ledger_checkpoints (
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                last_source_transition_id INTEGER NOT NULL CHECK (last_source_transition_id > 0),
                revision INTEGER NOT NULL CHECK (revision > 0),
                last_batch_fingerprint TEXT NOT NULL,
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
                    length(last_batch_fingerprint) = 64
                    AND last_batch_fingerprint = lower(last_batch_fingerprint)
                    AND last_batch_fingerprint NOT GLOB '*[^0-9a-f]*'
                )
            ) STRICT;

            CREATE TABLE canonical_payment_ledger_entries (
                entry_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (
                    kind IN ('canonical_payment', 'canonical_payment_reversal')
                ),
                source_transition_id INTEGER NOT NULL CHECK (source_transition_id > 0),
                source_checkpoint_revision INTEGER NOT NULL CHECK (source_checkpoint_revision > 0),
                block_number INTEGER NOT NULL CHECK (block_number >= 0),
                block_hash TEXT NOT NULL,
                transaction_hash TEXT NOT NULL,
                log_index INTEGER NOT NULL CHECK (log_index >= 0),
                payment_id TEXT NOT NULL,
                payer_address TEXT NOT NULL,
                token_address TEXT NOT NULL,
                merchant_address TEXT NOT NULL,
                amount_raw TEXT NOT NULL,
                reverses_entry_id INTEGER NULL,
                source_changed_at_utc TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                UNIQUE (
                    chain_id, router_address, source_transition_id,
                    block_hash, transaction_hash, log_index
                ),
                FOREIGN KEY (reverses_entry_id)
                    REFERENCES canonical_payment_ledger_entries (entry_id)
                    ON DELETE RESTRICT,
                CHECK (
                    (kind = 'canonical_payment' AND reverses_entry_id IS NULL)
                    OR
                    (kind = 'canonical_payment_reversal' AND reverses_entry_id IS NOT NULL)
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

            CREATE UNIQUE INDEX ux_ledger_single_reversal
                ON canonical_payment_ledger_entries (reverses_entry_id)
                WHERE reverses_entry_id IS NOT NULL;

            CREATE INDEX ix_ledger_occurrence
                ON canonical_payment_ledger_entries (
                    chain_id, router_address, block_hash,
                    transaction_hash, log_index, source_transition_id
                );

            CREATE INDEX ix_ledger_payment_id
                ON canonical_payment_ledger_entries (
                    chain_id, router_address, payment_id, source_transition_id
                );

            CREATE TRIGGER validate_ledger_reversal_reference
            BEFORE INSERT ON canonical_payment_ledger_entries
            WHEN NEW.kind = 'canonical_payment_reversal'
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1
                    FROM canonical_payment_ledger_entries AS credit
                    WHERE credit.entry_id = NEW.reverses_entry_id
                      AND credit.kind = 'canonical_payment'
                      AND credit.chain_id = NEW.chain_id
                      AND credit.router_address = NEW.router_address
                      AND credit.block_hash = NEW.block_hash
                      AND credit.transaction_hash = NEW.transaction_hash
                      AND credit.log_index = NEW.log_index
                      AND credit.source_transition_id < NEW.source_transition_id
                ) THEN RAISE(ABORT, 'invalid ledger reversal reference') END;
            END;
            """),
    ];
}

internal sealed record LedgerDatabaseMigration(long Version, string Name, string Sql);
