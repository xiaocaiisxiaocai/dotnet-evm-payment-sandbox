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
    ];
}

internal sealed record SiweChallengeDatabaseMigration(long Version, string Name, string Sql);
