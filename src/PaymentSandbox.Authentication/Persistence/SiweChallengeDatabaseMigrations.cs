namespace PaymentSandbox.Authentication.Persistence;

/// <summary>The ordered schema history owned only by the SIWE challenge database.</summary>
internal static class SiweChallengeDatabaseMigrations
{
    internal static readonly SiweChallengeDatabaseMigration[] All =
    [
        new(
            Version: 1,
            Name: "create_siwe_challenge_store",
            Sql:
            """
            CREATE TABLE siwe_store_settings (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                capacity INTEGER NOT NULL CHECK (capacity BETWEEN 1 AND 100000)
            ) STRICT;

            CREATE TABLE siwe_challenges (
                nonce TEXT NOT NULL PRIMARY KEY,
                domain TEXT NOT NULL,
                request_uri TEXT NOT NULL,
                chain_id TEXT NOT NULL CHECK (chain_id IN ('31337', '11155111')),
                statement TEXT NOT NULL,
                issued_at_unix_seconds INTEGER NOT NULL,
                expiration_at_unix_seconds INTEGER NOT NULL,
                policy_fingerprint TEXT NOT NULL,
                consumed_at_unix_milliseconds INTEGER NULL,
                CHECK (
                    length(nonce) = 32
                    AND nonce = lower(nonce)
                    AND nonce NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(domain) BETWEEN 1 AND 255
                    AND domain = lower(domain)
                    AND domain NOT GLOB '*[^a-z0-9.:-]*'
                ),
                CHECK (
                    length(request_uri) BETWEEN 9 AND 512
                    AND substr(request_uri, 1, 8) = 'https://'
                ),
                CHECK (length(statement) BETWEEN 1 AND 160),
                CHECK (expiration_at_unix_seconds > issued_at_unix_seconds),
                CHECK (
                    length(policy_fingerprint) = 64
                    AND policy_fingerprint = lower(policy_fingerprint)
                    AND policy_fingerprint NOT GLOB '*[^0-9a-f]*'
                )
            ) STRICT;

            CREATE INDEX ix_siwe_challenges_expiration
                ON siwe_challenges (expiration_at_unix_seconds);

            CREATE TRIGGER siwe_challenge_consumption_is_one_way
            BEFORE UPDATE ON siwe_challenges
            WHEN OLD.nonce IS NOT NEW.nonce
              OR OLD.domain IS NOT NEW.domain
              OR OLD.request_uri IS NOT NEW.request_uri
              OR OLD.chain_id IS NOT NEW.chain_id
              OR OLD.statement IS NOT NEW.statement
              OR OLD.issued_at_unix_seconds IS NOT NEW.issued_at_unix_seconds
              OR OLD.expiration_at_unix_seconds IS NOT NEW.expiration_at_unix_seconds
              OR OLD.policy_fingerprint IS NOT NEW.policy_fingerprint
              OR OLD.consumed_at_unix_milliseconds IS NOT NULL
              OR NEW.consumed_at_unix_milliseconds IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'SIWE challenge facts are immutable and consumption is one-way');
            END;

            CREATE TRIGGER siwe_store_capacity_is_immutable
            BEFORE UPDATE ON siwe_store_settings
            BEGIN
                SELECT RAISE(ABORT, 'SIWE store capacity is immutable');
            END;
            """),
        new(
            Version: 2,
            Name: "create_siwe_browser_sessions",
            Sql:
            """
            ALTER TABLE siwe_store_settings
                ADD COLUMN session_capacity INTEGER NULL
                CHECK (session_capacity BETWEEN 1 AND 100000);

            DROP TRIGGER siwe_store_capacity_is_immutable;

            CREATE TRIGGER siwe_store_capacity_is_immutable
            BEFORE UPDATE ON siwe_store_settings
            WHEN OLD.capacity IS NOT NEW.capacity
              OR OLD.session_capacity IS NOT NULL
              OR NEW.session_capacity IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'SIWE store capacities are immutable');
            END;

            CREATE TABLE siwe_login_flows (
                nonce TEXT NOT NULL PRIMARY KEY,
                binding_token_hash TEXT NOT NULL UNIQUE,
                expiration_at_unix_seconds INTEGER NOT NULL,
                consumed_at_unix_milliseconds INTEGER NULL,
                FOREIGN KEY (nonce) REFERENCES siwe_challenges (nonce) ON DELETE CASCADE,
                CHECK (
                    length(binding_token_hash) = 64
                    AND binding_token_hash = lower(binding_token_hash)
                    AND binding_token_hash NOT GLOB '*[^0-9a-f]*'
                )
            ) STRICT;

            CREATE INDEX ix_siwe_login_flows_expiration
                ON siwe_login_flows (expiration_at_unix_seconds);

            CREATE TRIGGER siwe_login_flow_consumption_is_one_way
            BEFORE UPDATE ON siwe_login_flows
            WHEN OLD.nonce IS NOT NEW.nonce
              OR OLD.binding_token_hash IS NOT NEW.binding_token_hash
              OR OLD.expiration_at_unix_seconds IS NOT NEW.expiration_at_unix_seconds
              OR OLD.consumed_at_unix_milliseconds IS NOT NULL
              OR NEW.consumed_at_unix_milliseconds IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'SIWE login flow facts are immutable and consumption is one-way');
            END;

            CREATE TABLE siwe_sessions (
                session_token_hash TEXT NOT NULL PRIMARY KEY,
                csrf_token_hash TEXT NOT NULL UNIQUE,
                address TEXT NOT NULL,
                chain_id TEXT NOT NULL CHECK (chain_id IN ('31337', '11155111')),
                created_at_unix_seconds INTEGER NOT NULL,
                expiration_at_unix_seconds INTEGER NOT NULL,
                revoked_at_unix_milliseconds INTEGER NULL,
                CHECK (
                    length(session_token_hash) = 64
                    AND session_token_hash = lower(session_token_hash)
                    AND session_token_hash NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(csrf_token_hash) = 64
                    AND csrf_token_hash = lower(csrf_token_hash)
                    AND csrf_token_hash NOT GLOB '*[^0-9a-f]*'
                ),
                CHECK (
                    length(address) = 42
                    AND substr(address, 1, 2) = '0x'
                    AND address = lower(address)
                    AND substr(address, 3) NOT GLOB '*[^0-9a-f]*'
                    AND address <> '0x0000000000000000000000000000000000000000'
                ),
                CHECK (expiration_at_unix_seconds > created_at_unix_seconds)
            ) STRICT;

            CREATE INDEX ix_siwe_sessions_expiration
                ON siwe_sessions (expiration_at_unix_seconds);

            CREATE TRIGGER siwe_session_revocation_is_one_way
            BEFORE UPDATE ON siwe_sessions
            WHEN OLD.session_token_hash IS NOT NEW.session_token_hash
              OR OLD.csrf_token_hash IS NOT NEW.csrf_token_hash
              OR OLD.address IS NOT NEW.address
              OR OLD.chain_id IS NOT NEW.chain_id
              OR OLD.created_at_unix_seconds IS NOT NEW.created_at_unix_seconds
              OR OLD.expiration_at_unix_seconds IS NOT NEW.expiration_at_unix_seconds
              OR OLD.revoked_at_unix_milliseconds IS NOT NULL
              OR NEW.revoked_at_unix_milliseconds IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'SIWE session facts are immutable and revocation is one-way');
            END;
            """),
    ];
}

internal sealed record SiweChallengeDatabaseMigration(long Version, string Name, string Sql);
