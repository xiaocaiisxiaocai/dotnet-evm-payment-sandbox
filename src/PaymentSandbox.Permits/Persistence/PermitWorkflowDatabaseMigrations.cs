namespace PaymentSandbox.Permits.Persistence;

internal static class PermitWorkflowDatabaseMigrations
{
    // Operations and preparations are immutable facts. State is never updated
    // in place: the validation trigger derives the legal predecessor from the
    // latest transition and the read path projects that append-only history.
    internal static readonly PermitWorkflowDatabaseMigration[] All =
    [
        new(1, "create_durable_erc2612_workflows",
            """
            CREATE TABLE permit_store_settings (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                capacity INTEGER NOT NULL CHECK (capacity BETWEEN 1 AND 100000)
            ) STRICT;

            CREATE TABLE permit_operations (
                operation_id TEXT NOT NULL PRIMARY KEY,
                policy_fingerprint TEXT NOT NULL,
                chain_id TEXT NOT NULL CHECK (chain_id IN ('31337', '11155111')),
                token_address TEXT NOT NULL,
                token_name TEXT NOT NULL,
                token_version TEXT NOT NULL,
                spender_address TEXT NOT NULL,
                owner_address TEXT NOT NULL,
                value_raw TEXT NOT NULL,
                token_nonce TEXT NOT NULL,
                issued_at_utc TEXT NOT NULL,
                deadline_utc TEXT NOT NULL,
                typed_data_json TEXT NOT NULL,
                domain_separator TEXT NOT NULL,
                struct_hash TEXT NOT NULL,
                digest TEXT NOT NULL,
                observed_block_number INTEGER NOT NULL CHECK (observed_block_number >= 0),
                observed_block_hash TEXT NOT NULL,
                runtime_code_hash TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE (chain_id, token_address, owner_address, token_nonce),
                CHECK (length(operation_id) = 32 AND operation_id NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(policy_fingerprint) = 64 AND policy_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(token_name) BETWEEN 1 AND 64),
                CHECK (length(token_version) BETWEEN 1 AND 16),
                CHECK (value_raw <> '' AND value_raw <> '0' AND value_raw NOT GLOB '*[^0-9]*'),
                CHECK (token_nonce <> '' AND token_nonce NOT GLOB '*[^0-9]*'),
                CHECK (length(typed_data_json) BETWEEN 1 AND 16384),
                CHECK (length(token_address) = 42 AND token_address = lower(token_address)
                    AND substr(token_address, 1, 2) = '0x' AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(spender_address) = 42 AND spender_address = lower(spender_address)
                    AND substr(spender_address, 1, 2) = '0x' AND substr(spender_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(owner_address) = 42 AND owner_address = lower(owner_address)
                    AND substr(owner_address, 1, 2) = '0x' AND substr(owner_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(domain_separator) = 66 AND domain_separator = lower(domain_separator)
                    AND substr(domain_separator, 1, 2) = '0x' AND substr(domain_separator, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(struct_hash) = 66 AND struct_hash = lower(struct_hash)
                    AND substr(struct_hash, 1, 2) = '0x' AND substr(struct_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(digest) = 66 AND digest = lower(digest)
                    AND substr(digest, 1, 2) = '0x' AND substr(digest, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(observed_block_hash) = 66 AND observed_block_hash = lower(observed_block_hash)
                    AND substr(observed_block_hash, 1, 2) = '0x' AND substr(observed_block_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(runtime_code_hash) = 66 AND runtime_code_hash = lower(runtime_code_hash)
                    AND substr(runtime_code_hash, 1, 2) = '0x' AND substr(runtime_code_hash, 3) NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE TABLE permit_preparations (
                operation_id TEXT NOT NULL PRIMARY KEY,
                payment_id TEXT NOT NULL,
                merchant_address TEXT NOT NULL,
                required_sender TEXT NOT NULL,
                calldata TEXT NOT NULL,
                calldata_hash TEXT NOT NULL,
                prepared_at_utc TEXT NOT NULL,
                FOREIGN KEY (operation_id) REFERENCES permit_operations (operation_id) ON DELETE RESTRICT,
                CHECK (length(payment_id) = 66 AND payment_id = lower(payment_id)
                    AND substr(payment_id, 1, 2) = '0x' AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(merchant_address) = 42 AND merchant_address = lower(merchant_address)
                    AND substr(merchant_address, 1, 2) = '0x' AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(required_sender) = 42 AND required_sender = lower(required_sender)
                    AND substr(required_sender, 1, 2) = '0x' AND substr(required_sender, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(calldata) = 522 AND calldata = lower(calldata)
                    AND substr(calldata, 1, 10) = '0x1f2b568e'
                    AND substr(calldata, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(calldata_hash) = 66 AND calldata_hash = lower(calldata_hash)
                    AND substr(calldata_hash, 1, 2) = '0x' AND substr(calldata_hash, 3) NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE TABLE permit_state_transitions (
                transition_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN (
                    'reserved', 'prepared', 'submission_unknown',
                    'submission_accepted', 'submission_rejected',
                    'nonce_changed', 'expired')),
                observed_block_number INTEGER NULL CHECK (observed_block_number IS NULL OR observed_block_number >= 0),
                observed_block_hash TEXT NULL,
                observed_nonce TEXT NULL,
                occurred_at_utc TEXT NOT NULL,
                FOREIGN KEY (operation_id) REFERENCES permit_operations (operation_id) ON DELETE RESTRICT,
                CHECK ((kind IN ('reserved', 'submission_unknown', 'nonce_changed')
                        AND observed_block_number IS NOT NULL
                        AND length(observed_block_hash) = 66
                        AND observed_block_hash = lower(observed_block_hash)
                        AND substr(observed_block_hash, 1, 2) = '0x'
                        AND substr(observed_block_hash, 3) NOT GLOB '*[^0-9a-f]*'
                        AND observed_nonce <> ''
                        AND observed_nonce NOT GLOB '*[^0-9]*')
                    OR (kind IN ('prepared', 'submission_accepted',
                            'submission_rejected', 'expired')
                        AND observed_block_number IS NULL
                        AND observed_block_hash IS NULL
                        AND observed_nonce IS NULL))
            ) STRICT;

            CREATE TRIGGER permit_store_settings_immutable
            BEFORE UPDATE ON permit_store_settings BEGIN
                SELECT RAISE(ABORT, 'permit store settings are immutable');
            END;
            CREATE TRIGGER permit_store_settings_no_delete
            BEFORE DELETE ON permit_store_settings BEGIN
                SELECT RAISE(ABORT, 'permit store settings cannot be deleted');
            END;
            CREATE TRIGGER permit_operations_immutable
            BEFORE UPDATE ON permit_operations BEGIN
                SELECT RAISE(ABORT, 'permit operation is immutable');
            END;
            CREATE TRIGGER permit_operations_no_delete
            BEFORE DELETE ON permit_operations BEGIN
                SELECT RAISE(ABORT, 'permit operation cannot be deleted');
            END;
            CREATE TRIGGER permit_preparations_immutable
            BEFORE UPDATE ON permit_preparations BEGIN
                SELECT RAISE(ABORT, 'permit preparation is immutable');
            END;
            CREATE TRIGGER permit_preparations_no_delete
            BEFORE DELETE ON permit_preparations BEGIN
                SELECT RAISE(ABORT, 'permit preparation cannot be deleted');
            END;
            CREATE TRIGGER permit_transitions_no_update
            BEFORE UPDATE ON permit_state_transitions BEGIN
                SELECT RAISE(ABORT, 'permit transition is append-only');
            END;
            CREATE TRIGGER permit_transitions_no_delete
            BEFORE DELETE ON permit_state_transitions BEGIN
                SELECT RAISE(ABORT, 'permit transition is append-only');
            END;

            CREATE TRIGGER validate_permit_transition
            BEFORE INSERT ON permit_state_transitions BEGIN
                SELECT CASE WHEN
                    (NEW.kind = 'reserved' AND EXISTS (
                        SELECT 1 FROM permit_state_transitions prior
                        WHERE prior.operation_id = NEW.operation_id))
                    OR (NEW.kind = 'prepared' AND (
                        NOT EXISTS (SELECT 1 FROM permit_preparations preparation
                            WHERE preparation.operation_id = NEW.operation_id)
                        OR COALESCE((SELECT kind FROM permit_state_transitions prior
                            WHERE prior.operation_id = NEW.operation_id
                            ORDER BY transition_id DESC LIMIT 1), '') <> 'reserved'))
                    OR (NEW.kind = 'submission_unknown' AND
                        COALESCE((SELECT kind FROM permit_state_transitions prior
                            WHERE prior.operation_id = NEW.operation_id
                            ORDER BY transition_id DESC LIMIT 1), '')
                            NOT IN ('prepared', 'submission_unknown'))
                    OR (NEW.kind IN ('submission_accepted', 'submission_rejected') AND
                        COALESCE((SELECT kind FROM permit_state_transitions prior
                            WHERE prior.operation_id = NEW.operation_id
                            ORDER BY transition_id DESC LIMIT 1), '') <> 'submission_unknown')
                    OR (NEW.kind IN ('nonce_changed', 'expired') AND
                        COALESCE((SELECT kind FROM permit_state_transitions prior
                            WHERE prior.operation_id = NEW.operation_id
                            ORDER BY transition_id DESC LIMIT 1), '')
                            NOT IN ('reserved', 'prepared', 'submission_unknown', 'submission_accepted'))
                THEN RAISE(ABORT, 'invalid permit state transition') END;
            END;

            CREATE INDEX ix_permit_transition_history
                ON permit_state_transitions (operation_id, transition_id);
            """),
    ];
}

internal sealed record PermitWorkflowDatabaseMigration(long Version, string Name, string Sql);
