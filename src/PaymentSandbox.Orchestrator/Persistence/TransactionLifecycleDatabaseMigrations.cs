namespace PaymentSandbox.Orchestrator.Persistence;

internal static class TransactionLifecycleDatabaseMigrations
{
    internal static readonly TransactionLifecycleDatabaseMigration[] All =
    [
        new(1, "create_append_only_transaction_lifecycles",
            """
            CREATE TABLE transaction_operations (
                operation_id TEXT NOT NULL PRIMARY KEY,
                request_fingerprint TEXT NOT NULL,
                chain_id TEXT NOT NULL,
                signer_address TEXT NOT NULL,
                router_address TEXT NOT NULL,
                payment_id TEXT NOT NULL,
                token_address TEXT NOT NULL,
                merchant_address TEXT NOT NULL,
                amount_raw TEXT NOT NULL,
                nonce INTEGER NOT NULL CHECK (nonce >= 0),
                observed_pending_nonce INTEGER NOT NULL CHECK (observed_pending_nonce >= 0),
                gas_limit INTEGER NOT NULL CHECK (gas_limit > 0),
                calldata TEXT NOT NULL,
                policy_id TEXT NOT NULL,
                policy_fingerprint TEXT NOT NULL,
                max_gas_limit INTEGER NOT NULL CHECK (max_gas_limit >= gas_limit),
                initial_max_fee_per_gas_wei TEXT NOT NULL,
                initial_max_priority_fee_per_gas_wei TEXT NOT NULL,
                max_fee_per_gas_wei TEXT NOT NULL,
                max_priority_fee_per_gas_wei TEXT NOT NULL,
                minimum_fee_bump_basis_points INTEGER NOT NULL CHECK (minimum_fee_bump_basis_points > 0),
                max_attempts INTEGER NOT NULL CHECK (max_attempts > 0),
                max_reserved_nonce_lead INTEGER NOT NULL CHECK (max_reserved_nonce_lead >= 0),
                created_at_utc TEXT NOT NULL,
                UNIQUE (chain_id, signer_address, nonce),
                CHECK (length(operation_id) BETWEEN 1 AND 64
                    AND operation_id NOT GLOB '*[^A-Za-z0-9._:-]*'),
                CHECK (length(request_fingerprint) = 64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(chain_id) > 0 AND chain_id <> '0' AND chain_id NOT GLOB '*[^0-9]*'),
                CHECK (chain_id <> '1'),
                CHECK (length(signer_address) = 42 AND signer_address = lower(signer_address)
                    AND substr(signer_address, 1, 2) = '0x' AND substr(signer_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(router_address) = 42 AND router_address = lower(router_address)
                    AND substr(router_address, 1, 2) = '0x' AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(token_address) = 42 AND token_address = lower(token_address)
                    AND substr(token_address, 1, 2) = '0x' AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(merchant_address) = 42 AND merchant_address = lower(merchant_address)
                    AND substr(merchant_address, 1, 2) = '0x' AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(payment_id) = 66 AND payment_id = lower(payment_id)
                    AND substr(payment_id, 1, 2) = '0x' AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (amount_raw <> '' AND amount_raw <> '0' AND amount_raw NOT GLOB '*[^0-9]*'),
                CHECK (length(calldata) = 266 AND calldata = lower(calldata)
                    AND substr(calldata, 1, 10) = '0x76bbf425'
                    AND substr(calldata, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(policy_id) BETWEEN 1 AND 64
                    AND policy_id NOT GLOB '*[^A-Za-z0-9._-]*'),
                CHECK (length(policy_fingerprint) = 64 AND policy_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (max_fee_per_gas_wei <> '' AND max_fee_per_gas_wei NOT GLOB '*[^0-9]*'),
                CHECK (initial_max_fee_per_gas_wei <> ''
                    AND initial_max_fee_per_gas_wei NOT GLOB '*[^0-9]*'),
                CHECK (initial_max_priority_fee_per_gas_wei <> ''
                    AND initial_max_priority_fee_per_gas_wei NOT GLOB '*[^0-9]*'),
                CHECK (max_priority_fee_per_gas_wei <> ''
                    AND max_priority_fee_per_gas_wei NOT GLOB '*[^0-9]*')
            ) STRICT;

            CREATE TABLE transaction_attempts (
                attempt_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                nonce INTEGER NOT NULL CHECK (nonce >= 0),
                max_fee_per_gas_wei TEXT NOT NULL,
                max_priority_fee_per_gas_wei TEXT NOT NULL,
                raw_transaction TEXT NOT NULL,
                transaction_hash TEXT NOT NULL UNIQUE,
                signed_byte_length INTEGER NOT NULL CHECK (signed_byte_length BETWEEN 1 AND 16384),
                unsigned_fingerprint TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE (operation_id, sequence),
                FOREIGN KEY (operation_id) REFERENCES transaction_operations (operation_id) ON DELETE RESTRICT,
                CHECK (max_fee_per_gas_wei <> '' AND max_fee_per_gas_wei NOT GLOB '*[^0-9]*'),
                CHECK (max_priority_fee_per_gas_wei <> ''
                    AND max_priority_fee_per_gas_wei NOT GLOB '*[^0-9]*'),
                CHECK (length(raw_transaction) BETWEEN 4 AND 32770
                    AND length(raw_transaction) % 2 = 0 AND raw_transaction = lower(raw_transaction)
                    AND substr(raw_transaction, 1, 2) = '0x'
                    AND substr(raw_transaction, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(transaction_hash) = 66 AND transaction_hash = lower(transaction_hash)
                    AND substr(transaction_hash, 1, 2) = '0x'
                    AND substr(transaction_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(unsigned_fingerprint) = 64
                    AND unsigned_fingerprint NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE TABLE transaction_broadcast_observations (
                broadcast_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                attempt_id INTEGER NOT NULL,
                outcome TEXT NOT NULL CHECK (outcome IN ('accepted', 'already_known', 'unknown', 'rejected')),
                code TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                FOREIGN KEY (attempt_id) REFERENCES transaction_attempts (attempt_id) ON DELETE RESTRICT,
                CHECK (length(code) BETWEEN 1 AND 64 AND code NOT GLOB '*[^a-z0-9._-]*')
            ) STRICT;

            CREATE TABLE transaction_receipt_observations (
                receipt_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL UNIQUE,
                attempt_id INTEGER NOT NULL UNIQUE,
                transaction_hash TEXT NOT NULL UNIQUE,
                execution_status TEXT NOT NULL CHECK (execution_status IN ('succeeded', 'reverted')),
                block_number INTEGER NOT NULL CHECK (block_number >= 0),
                block_hash TEXT NOT NULL,
                gas_used INTEGER NOT NULL CHECK (gas_used > 0),
                effective_gas_price_wei TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                FOREIGN KEY (operation_id) REFERENCES transaction_operations (operation_id) ON DELETE RESTRICT,
                FOREIGN KEY (attempt_id) REFERENCES transaction_attempts (attempt_id) ON DELETE RESTRICT,
                CHECK (length(transaction_hash) = 66 AND transaction_hash = lower(transaction_hash)
                    AND substr(transaction_hash, 1, 2) = '0x'
                    AND substr(transaction_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(block_hash) = 66 AND block_hash = lower(block_hash)
                    AND substr(block_hash, 1, 2) = '0x'
                    AND substr(block_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (effective_gas_price_wei <> ''
                    AND effective_gas_price_wei NOT GLOB '*[^0-9]*')
            ) STRICT;

            CREATE TRIGGER validate_transaction_attempt
            BEFORE INSERT ON transaction_attempts
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1 FROM transaction_operations AS operation
                    WHERE operation.operation_id = NEW.operation_id
                      AND operation.nonce = NEW.nonce
                      AND NEW.sequence = (
                          SELECT COUNT(*) + 1 FROM transaction_attempts AS prior
                          WHERE prior.operation_id = NEW.operation_id)
                      AND NEW.sequence <= operation.max_attempts
                      AND NOT EXISTS (
                          SELECT 1 FROM transaction_receipt_observations AS receipt
                          WHERE receipt.operation_id = NEW.operation_id)
                ) THEN RAISE(ABORT, 'invalid transaction attempt') END;
            END;

            CREATE TRIGGER validate_transaction_receipt
            BEFORE INSERT ON transaction_receipt_observations
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1 FROM transaction_attempts AS attempt
                    WHERE attempt.attempt_id = NEW.attempt_id
                      AND attempt.operation_id = NEW.operation_id
                      AND attempt.transaction_hash = NEW.transaction_hash
                      AND EXISTS (
                          SELECT 1 FROM transaction_broadcast_observations AS broadcast
                          WHERE broadcast.attempt_id = attempt.attempt_id
                            AND broadcast.outcome IN ('accepted', 'already_known', 'unknown'))
                ) THEN RAISE(ABORT, 'invalid transaction receipt') END;
            END;

            CREATE TRIGGER validate_transaction_broadcast
            BEFORE INSERT ON transaction_broadcast_observations
            BEGIN
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM transaction_attempts AS attempt
                    JOIN transaction_receipt_observations AS receipt
                      ON receipt.operation_id = attempt.operation_id
                    WHERE attempt.attempt_id = NEW.attempt_id
                ) THEN RAISE(ABORT, 'transaction operation already has a receipt') END;
            END;

            CREATE INDEX ix_transaction_attempt_history
                ON transaction_attempts (operation_id, sequence);
            CREATE INDEX ix_transaction_broadcast_history
                ON transaction_broadcast_observations (attempt_id, broadcast_id);
            """),
    ];
}

internal sealed record TransactionLifecycleDatabaseMigration(long Version, string Name, string Sql);
