namespace PaymentSandbox.Reconciliation.Persistence;

internal static class ReconciliationDatabaseMigrations
{
    internal static readonly ReconciliationDatabaseMigration[] All =
    [
        new(1, "create_append_only_reconciliation_reports",
            """
            CREATE TABLE reconciliation_reports (
                report_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                payment_id TEXT NOT NULL,
                chain_id TEXT NOT NULL,
                router_address TEXT NOT NULL,
                policy_id TEXT NOT NULL,
                policy_fingerprint TEXT NOT NULL,
                intent_publication_high_watermark INTEGER NOT NULL CHECK (intent_publication_high_watermark >= 0),
                intent_publication_id INTEGER NULL CHECK (intent_publication_id IS NULL OR intent_publication_id > 0),
                intent_chain_id TEXT NULL,
                intent_token_address TEXT NULL,
                intent_merchant_address TEXT NULL,
                intent_amount_raw TEXT NULL,
                intent_created_at_utc TEXT NULL,
                ledger_entry_high_watermark INTEGER NOT NULL CHECK (ledger_entry_high_watermark >= 0),
                ledger_checkpoint_revision INTEGER NOT NULL CHECK (ledger_checkpoint_revision > 0),
                ledger_source_transition_id INTEGER NOT NULL CHECK (ledger_source_transition_id > 0),
                finality_transition_high_watermark INTEGER NOT NULL CHECK (finality_transition_high_watermark >= 0),
                finality_revision INTEGER NOT NULL CHECK (finality_revision > 0),
                finality_policy_fingerprint TEXT NOT NULL,
                is_consistent INTEGER NOT NULL CHECK (is_consistent IN (0, 1)),
                canonical_occurrence_count INTEGER NOT NULL CHECK (canonical_occurrence_count >= 0),
                active_occurrence_count INTEGER NOT NULL CHECK (active_occurrence_count >= 0),
                matching_active_occurrence_count INTEGER NOT NULL CHECK (matching_active_occurrence_count >= 0),
                qualified_matching_occurrence_count INTEGER NOT NULL CHECK (qualified_matching_occurrence_count >= 0),
                matching_active_amount_raw TEXT NOT NULL,
                qualified_matching_amount_raw TEXT NOT NULL,
                batch_fingerprint TEXT NOT NULL,
                evaluated_at_utc TEXT NOT NULL,
                UNIQUE (payment_id, policy_fingerprint, intent_publication_high_watermark,
                    ledger_entry_high_watermark, finality_revision, finality_transition_high_watermark),
                CHECK ((intent_publication_id IS NULL AND intent_chain_id IS NULL
                        AND intent_token_address IS NULL AND intent_merchant_address IS NULL
                        AND intent_amount_raw IS NULL AND intent_created_at_utc IS NULL)
                    OR (intent_publication_id IS NOT NULL AND intent_chain_id IS NOT NULL
                        AND intent_token_address IS NOT NULL AND intent_merchant_address IS NOT NULL
                        AND intent_amount_raw IS NOT NULL AND intent_created_at_utc IS NOT NULL)),
                CHECK (length(payment_id) = 66 AND substr(payment_id, 1, 2) = '0x'
                    AND payment_id = lower(payment_id) AND substr(payment_id, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(chain_id) > 0 AND chain_id NOT GLOB '*[^0-9]*' AND chain_id <> '0'),
                CHECK (length(router_address) = 42 AND router_address = lower(router_address)
                    AND substr(router_address, 1, 2) = '0x' AND substr(router_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(policy_id) BETWEEN 1 AND 64
                    AND policy_id NOT GLOB '*[^A-Za-z0-9._-]*'),
                CHECK (length(policy_fingerprint) = 64 AND policy_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(finality_policy_fingerprint) = 64 AND finality_policy_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(batch_fingerprint) = 64 AND batch_fingerprint NOT GLOB '*[^0-9a-f]*'),
                CHECK (matching_active_amount_raw <> '' AND matching_active_amount_raw NOT GLOB '*[^0-9]*'),
                CHECK (qualified_matching_amount_raw <> '' AND qualified_matching_amount_raw NOT GLOB '*[^0-9]*')
            ) STRICT;

            CREATE TABLE reconciliation_report_ledger_entries (
                report_id INTEGER NOT NULL,
                entry_id INTEGER NOT NULL CHECK (entry_id > 0),
                kind TEXT NOT NULL CHECK (kind IN ('canonical_payment', 'canonical_payment_reversal')),
                source_transition_id INTEGER NOT NULL CHECK (source_transition_id > 0),
                source_checkpoint_revision INTEGER NOT NULL CHECK (source_checkpoint_revision > 0),
                block_number INTEGER NOT NULL CHECK (block_number >= 0),
                block_hash TEXT NOT NULL,
                transaction_hash TEXT NOT NULL,
                log_index INTEGER NOT NULL CHECK (log_index >= 0),
                payer_address TEXT NOT NULL,
                token_address TEXT NOT NULL,
                merchant_address TEXT NOT NULL,
                amount_raw TEXT NOT NULL,
                reverses_entry_id INTEGER NULL,
                source_changed_at_utc TEXT NOT NULL,
                ledger_recorded_at_utc TEXT NOT NULL,
                PRIMARY KEY (report_id, entry_id),
                FOREIGN KEY (report_id) REFERENCES reconciliation_reports (report_id) ON DELETE RESTRICT,
                CHECK ((kind = 'canonical_payment' AND reverses_entry_id IS NULL)
                    OR (kind = 'canonical_payment_reversal' AND reverses_entry_id > 0)),
                CHECK (length(block_hash) = 66 AND block_hash = lower(block_hash)
                    AND substr(block_hash, 1, 2) = '0x' AND substr(block_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(transaction_hash) = 66 AND transaction_hash = lower(transaction_hash)
                    AND substr(transaction_hash, 1, 2) = '0x' AND substr(transaction_hash, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(payer_address) = 42 AND payer_address = lower(payer_address)
                    AND substr(payer_address, 1, 2) = '0x' AND substr(payer_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(token_address) = 42 AND token_address = lower(token_address)
                    AND substr(token_address, 1, 2) = '0x' AND substr(token_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (length(merchant_address) = 42 AND merchant_address = lower(merchant_address)
                    AND substr(merchant_address, 1, 2) = '0x' AND substr(merchant_address, 3) NOT GLOB '*[^0-9a-f]*'),
                CHECK (amount_raw <> '' AND amount_raw NOT GLOB '*[^0-9]*')
            ) STRICT;

            CREATE TABLE reconciliation_report_finality_transitions (
                report_id INTEGER NOT NULL,
                transition_id INTEGER NOT NULL CHECK (transition_id > 0),
                finality_revision INTEGER NOT NULL CHECK (finality_revision > 0),
                kind TEXT NOT NULL CHECK (kind IN ('confirmation_qualified', 'confirmation_revoked')),
                ledger_effect_entry_id INTEGER NOT NULL CHECK (ledger_effect_entry_id > 0),
                revokes_transition_id INTEGER NULL,
                head_block_number INTEGER NOT NULL CHECK (head_block_number >= 0),
                head_block_hash TEXT NOT NULL,
                head_checkpoint_revision INTEGER NOT NULL CHECK (head_checkpoint_revision > 0),
                confirmation_count INTEGER NOT NULL CHECK (confirmation_count >= 0),
                required_confirmation_count INTEGER NOT NULL CHECK (required_confirmation_count > 0),
                reason TEXT NOT NULL,
                finality_recorded_at_utc TEXT NOT NULL,
                PRIMARY KEY (report_id, transition_id),
                FOREIGN KEY (report_id) REFERENCES reconciliation_reports (report_id) ON DELETE RESTRICT,
                CHECK ((kind = 'confirmation_qualified' AND revokes_transition_id IS NULL
                        AND reason = 'confirmation_threshold_reached')
                    OR (kind = 'confirmation_revoked' AND revokes_transition_id > 0
                        AND reason IN ('ledger_effect_reversed', 'confirmation_threshold_lost'))),
                CHECK (length(head_block_hash) = 66 AND head_block_hash = lower(head_block_hash)
                    AND substr(head_block_hash, 1, 2) = '0x'
                    AND substr(head_block_hash, 3) NOT GLOB '*[^0-9a-f]*')
            ) STRICT;

            CREATE TABLE reconciliation_report_discrepancies (
                report_id INTEGER NOT NULL,
                code TEXT NOT NULL CHECK (code IN (
                    'intent_missing', 'active_payment_missing', 'reversed_payment_history',
                    'chain_mismatch', 'token_mismatch', 'merchant_mismatch',
                    'amount_underpaid', 'amount_overpaid', 'qualification_incomplete')),
                PRIMARY KEY (report_id, code),
                FOREIGN KEY (report_id) REFERENCES reconciliation_reports (report_id) ON DELETE RESTRICT
            ) STRICT;

            CREATE INDEX ix_reconciliation_payment_history
                ON reconciliation_reports (payment_id, report_id);
            """),
    ];
}

internal sealed record ReconciliationDatabaseMigration(long Version, string Name, string Sql);
