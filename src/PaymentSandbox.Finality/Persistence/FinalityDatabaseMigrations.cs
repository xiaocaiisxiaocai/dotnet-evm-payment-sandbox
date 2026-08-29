namespace PaymentSandbox.Finality.Persistence;

internal static class FinalityDatabaseMigrations
{
    internal static readonly FinalityDatabaseMigration[] All =
    [
        new(
            1,
            "create_confirmation_finality_projection",
            """
            CREATE TABLE finality_checkpoints (
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                last_ledger_entry_id INTEGER NOT NULL CHECK (last_ledger_entry_id >= 0),
                ledger_checkpoint_revision INTEGER NOT NULL CHECK (ledger_checkpoint_revision > 0),
                last_indexer_transition_id INTEGER NOT NULL CHECK (last_indexer_transition_id > 0),
                head_block_number INTEGER NOT NULL CHECK (head_block_number >= 0),
                head_block_hash TEXT NOT NULL,
                head_checkpoint_revision INTEGER NOT NULL CHECK (head_checkpoint_revision > 0),
                revision INTEGER NOT NULL CHECK (revision > 0),
                policy_id TEXT NOT NULL CHECK (
                    length(policy_id) BETWEEN 1 AND 64
                    AND policy_id NOT GLOB '*[^A-Za-z0-9._-]*'),
                required_confirmation_count INTEGER NOT NULL CHECK (
                    required_confirmation_count > 0),
                policy_fingerprint TEXT NOT NULL,
                last_batch_fingerprint TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (chain_id, router_address),
                CHECK (length(chain_id) > 0 AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0' AND substr(chain_id, 1, 1) <> '0'),
                CHECK (length(router_address) = 42 AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND router_address <> '0x0000000000000000000000000000000000000000'),
                CHECK (length(head_block_hash) = 66 AND substr(head_block_hash, 1, 2) = '0x'
                    AND head_block_hash = lower(head_block_hash)
                    AND substr(head_block_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(policy_fingerprint) = 64 AND policy_fingerprint = lower(policy_fingerprint)
                    AND policy_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(last_batch_fingerprint) = 64
                    AND last_batch_fingerprint = lower(last_batch_fingerprint)
                    AND last_batch_fingerprint NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE TABLE finality_source_ledger_entries (
                ledger_entry_id INTEGER NOT NULL PRIMARY KEY CHECK (ledger_entry_id > 0),
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('canonical_payment', 'canonical_payment_reversal')),
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
                reverses_ledger_entry_id INTEGER NULL CHECK (
                    reverses_ledger_entry_id IS NULL OR reverses_ledger_entry_id > 0),
                source_changed_at_utc TEXT NOT NULL,
                ledger_recorded_at_utc TEXT NOT NULL,
                FOREIGN KEY (reverses_ledger_entry_id)
                    REFERENCES finality_source_ledger_entries (ledger_entry_id) ON DELETE RESTRICT,
                CHECK ((kind = 'canonical_payment' AND reverses_ledger_entry_id IS NULL)
                    OR (kind = 'canonical_payment_reversal' AND reverses_ledger_entry_id IS NOT NULL)),
                CHECK (length(chain_id) > 0 AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0' AND substr(chain_id, 1, 1) <> '0'),
                CHECK (length(router_address) = 42 AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(block_hash) = 66 AND substr(block_hash, 1, 2) = '0x'
                    AND block_hash = lower(block_hash)
                    AND substr(block_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(transaction_hash) = 66 AND substr(transaction_hash, 1, 2) = '0x'
                    AND transaction_hash = lower(transaction_hash)
                    AND substr(transaction_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(payment_id) = 66 AND substr(payment_id, 1, 2) = '0x'
                    AND payment_id = lower(payment_id)
                    AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'
                    AND payment_id <> '0x0000000000000000000000000000000000000000000000000000000000000000'),
                CHECK (length(payer_address) = 42 AND substr(payer_address, 1, 2) = '0x'
                    AND payer_address = lower(payer_address)
                    AND substr(payer_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND payer_address <> '0x0000000000000000000000000000000000000000'),
                CHECK (length(token_address) = 42 AND substr(token_address, 1, 2) = '0x'
                    AND token_address = lower(token_address)
                    AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND token_address <> '0x0000000000000000000000000000000000000000'),
                CHECK (length(merchant_address) = 42 AND substr(merchant_address, 1, 2) = '0x'
                    AND merchant_address = lower(merchant_address)
                    AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND merchant_address <> '0x0000000000000000000000000000000000000000'),
                CHECK (length(amount_raw) > 0 AND amount_raw NOT GLOB '*[^0-9]*'
                    AND amount_raw <> '0' AND substr(amount_raw, 1, 1) <> '0'
                    AND length(amount_raw) <= 78
                    AND (length(amount_raw) < 78 OR amount_raw <=
                        '115792089237316195423570985008687907853269984665640564039457584007913129639935'))
            ) STRICT;

            CREATE TABLE payment_finality_transitions (
                transition_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                finality_revision INTEGER NOT NULL CHECK (finality_revision > 0),
                kind TEXT NOT NULL CHECK (kind IN ('confirmation_qualified', 'confirmation_revoked')),
                ledger_effect_entry_id INTEGER NOT NULL CHECK (ledger_effect_entry_id > 0),
                revokes_transition_id INTEGER NULL CHECK (
                    revokes_transition_id IS NULL OR revokes_transition_id > 0),
                head_block_number INTEGER NOT NULL CHECK (head_block_number >= 0),
                head_block_hash TEXT NOT NULL,
                head_checkpoint_revision INTEGER NOT NULL CHECK (head_checkpoint_revision > 0),
                confirmation_count INTEGER NOT NULL CHECK (confirmation_count >= 0),
                required_confirmation_count INTEGER NOT NULL CHECK (required_confirmation_count > 0),
                reason TEXT NOT NULL CHECK (reason IN (
                    'confirmation_threshold_reached',
                    'ledger_effect_reversed',
                    'confirmation_threshold_lost')),
                recorded_at_utc TEXT NOT NULL,
                UNIQUE (chain_id, router_address, finality_revision, ledger_effect_entry_id),
                FOREIGN KEY (ledger_effect_entry_id)
                    REFERENCES finality_source_ledger_entries (ledger_entry_id) ON DELETE RESTRICT,
                FOREIGN KEY (revokes_transition_id)
                    REFERENCES payment_finality_transitions (transition_id) ON DELETE RESTRICT,
                CHECK ((kind = 'confirmation_qualified' AND revokes_transition_id IS NULL
                        AND reason = 'confirmation_threshold_reached')
                    OR (kind = 'confirmation_revoked' AND revokes_transition_id IS NOT NULL
                        AND reason <> 'confirmation_threshold_reached')),
                CHECK ((reason = 'confirmation_threshold_reached'
                        AND confirmation_count >= required_confirmation_count)
                    OR (reason = 'confirmation_threshold_lost'
                        AND confirmation_count < required_confirmation_count)
                    OR reason = 'ledger_effect_reversed'),
                CHECK (length(chain_id) > 0 AND chain_id NOT GLOB '*[^0-9]*'
                    AND chain_id <> '0' AND substr(chain_id, 1, 1) <> '0'),
                CHECK (length(router_address) = 42 AND substr(router_address, 1, 2) = '0x'
                    AND router_address = lower(router_address)
                    AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(head_block_hash) = 66 AND substr(head_block_hash, 1, 2) = '0x'
                    AND head_block_hash = lower(head_block_hash)
                    AND substr(head_block_hash, 3) NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE UNIQUE INDEX ux_finality_single_revocation
                ON payment_finality_transitions (revokes_transition_id)
                WHERE revokes_transition_id IS NOT NULL;
            CREATE INDEX ix_finality_effect_history
                ON payment_finality_transitions (
                    chain_id, router_address, ledger_effect_entry_id, transition_id);
            CREATE INDEX ix_finality_source_stream
                ON finality_source_ledger_entries (chain_id, router_address, ledger_entry_id);

            CREATE TRIGGER validate_finality_source_reversal
            BEFORE INSERT ON finality_source_ledger_entries
            WHEN NEW.kind = 'canonical_payment_reversal'
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1 FROM finality_source_ledger_entries AS effect
                    WHERE effect.ledger_entry_id = NEW.reverses_ledger_entry_id
                      AND effect.kind = 'canonical_payment'
                      AND effect.chain_id = NEW.chain_id
                      AND effect.router_address = NEW.router_address
                      AND effect.block_hash = NEW.block_hash
                      AND effect.transaction_hash = NEW.transaction_hash
                      AND effect.log_index = NEW.log_index
                      AND effect.ledger_entry_id < NEW.ledger_entry_id
                ) THEN RAISE(ABORT, 'invalid finality source reversal') END;
            END;

            CREATE TRIGGER validate_finality_revocation
            BEFORE INSERT ON payment_finality_transitions
            WHEN NEW.kind = 'confirmation_revoked'
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1 FROM payment_finality_transitions AS qualified
                    WHERE qualified.transition_id = NEW.revokes_transition_id
                      AND qualified.kind = 'confirmation_qualified'
                      AND qualified.chain_id = NEW.chain_id
                      AND qualified.router_address = NEW.router_address
                      AND qualified.ledger_effect_entry_id = NEW.ledger_effect_entry_id
                      AND qualified.finality_revision < NEW.finality_revision
                ) THEN RAISE(ABORT, 'invalid finality revocation reference') END;
            END;

            CREATE TRIGGER validate_finality_qualification
            BEFORE INSERT ON payment_finality_transitions
            WHEN NEW.kind = 'confirmation_qualified'
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1 FROM finality_source_ledger_entries AS effect
                    WHERE effect.ledger_entry_id = NEW.ledger_effect_entry_id
                      AND effect.kind = 'canonical_payment'
                      AND effect.chain_id = NEW.chain_id
                      AND effect.router_address = NEW.router_address
                      AND NOT EXISTS (
                          SELECT 1 FROM finality_source_ledger_entries AS reversal
                          WHERE reversal.reverses_ledger_entry_id = effect.ledger_entry_id
                      )
                ) THEN RAISE(ABORT, 'invalid finality qualification source') END;
            END;
            """),
    ];
}

internal sealed record FinalityDatabaseMigration(long Version, string Name, string Sql);
