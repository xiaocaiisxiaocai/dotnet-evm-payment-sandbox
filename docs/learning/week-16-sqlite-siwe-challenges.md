# Week 16: durable SQLite SIWE challenges

Week 16 replaces the Week 15 process-local replay boundary with a second
`ISiweChallengeStore` implementation. `SqliteSiweChallengeStore` preserves an
issued challenge and its consumed state across application restarts, and uses a
shared SQLite file to arbitrate concurrent local processes.

This is persistence and coordination, not a login endpoint. There is still no
HTTP origin source, browser binding, cookie, session, user, role, tenant, or
payment authorization.

## Why a separate database boundary exists

An in-memory lock can make one process correct, but it cannot answer either of
these cases:

1. the process restarts after issuing a challenge but before verification; or
2. two application processes receive the same signed proof.

The SQLite implementation moves the issued-to-consumed transition into a local
database transaction. Every process that uses the same file now observes one
durable nonce state. This does not coordinate two hosts with separate files and
does not make a replicated/network filesystem safe.

The challenge database has its own path and migration history. It must not be
silently mixed with the Intent, Indexer, Ledger, Finality, Reconciliation, or
transaction-lifecycle databases.

## What is persisted

The `siwe_challenges` table stores only server-issued facts:

- nonce;
- relying-party domain and request URI;
- chain ID and exact statement;
- issued-at and expiration Unix seconds;
- complete policy fingerprint; and
- nullable consumption time.

It deliberately stores no wallet address, SIWE plaintext, signature, cookie, or
authorization result. The wallet address is added to the displayed message and
proved by ERC-191 recovery at verification time. Avoiding signature storage
reduces replay material and avoids inventing an audit/user model before one has
been designed.

This also means the database does not record which address consumed a nonce.
That is a deliberate Week 16 data-minimization boundary, not a complete login
audit trail.

## Migration-owned schema

`SiweChallengeDatabase.InitializeAsync` creates the parent directory, enables
WAL mode and a five-second busy timeout, then applies ordered migrations inside
an immediate serializable transaction.

Migration 1 creates:

- `schema_migrations`, the shared convention for this dedicated database file;
- `siwe_store_settings`, one immutable capacity value;
- `siwe_challenges`, a SQLite `STRICT` table;
- an expiration index; and
- a trigger that permits only the transition from unconsumed to consumed.

Challenge columns have constraints for the 32-character lowercase hexadecimal
server nonce, supported chain allowlist, bounded HTTPS URI and statement,
strictly increasing issued/expiry seconds, and lowercase SHA-256 policy
fingerprint. Constraints are defense in depth; the canonical parser and policy
remain the semantic validators.

Initialization rejects a migration version newer than the code understands or
a known version with another name. Concurrent initializers serialize and record
migration 1 once.

## Database-owned capacity

Capacity is not left as an unverified per-process interpretation. The first
initializer inserts the configured value into the singleton
`siwe_store_settings` row. Every later initializer must provide the same value.
A mismatch fails startup.

This matters because two processes configured with different limits could
otherwise disagree about whether the same file is full. The schema accepts only
1 through 100,000 rows as the configured bound. Changing the bound requires an
explicit future migration rather than an unnoticed runtime update.

## Issuing a challenge

`TryAddAsync` starts an immediate transaction before it reads anything. The
sequence is:

1. acquire the SQLite writer reservation;
2. reject an existing nonce, even if that old row could be cleaned;
3. count rows;
4. only at capacity, delete consumed or already expired rows;
5. recheck capacity;
6. insert the complete challenge and commit.

Cleanup uses the new challenge's server-issued time, not an untrusted client
timestamp. A backward-moving server clock can retain rows and cause a safe
capacity failure; it cannot make a still-active row eligible for deletion.

Deleting old consumed rows can change a later replay response from
`ChallengeAlreadyUsed` to `ChallengeNotFound`. Both reject authentication.

## Consuming a challenge

The critical state change is one conditional SQL update:

```sql
UPDATE siwe_challenges
SET consumed_at_unix_milliseconds = $observedAt
WHERE nonce = $nonce
  AND consumed_at_unix_milliseconds IS NULL
  AND $observedAt < expiration_at_unix_seconds * 1000
  AND domain = $domain
  AND request_uri = $requestUri
  AND chain_id = $chainId
  AND statement = $statement
  AND issued_at_unix_seconds = $issuedAt
  AND expiration_at_unix_seconds = $expiration
  AND policy_fingerprint = $policyFingerprint;
```

The transaction is immediate, so the writer reservation is acquired before the
update. One verifier changes one row and commits. A concurrent verifier waits,
then evaluates the predicate against the consumed row and changes zero rows.
The store classifies that durable state as `AlreadyConsumed`.

This avoids a `SELECT unused -> UPDATE used` race. The exact signed facts and
the policy fingerprint are part of the update predicate, so a shifted timestamp
or old-policy challenge cannot consume the stored row.

Expiration is exclusive. At the exact `Expiration Time`, the comparison is
false and authentication returns `ChallengeExpired`.

## One-way schema trigger

Application logic is not the only guard. The update trigger rejects:

- changes to any issued challenge fact;
- changing a consumed row again; and
- changing consumption back to `NULL`.

The legitimate store update is therefore the only supported row mutation:
all facts stay equal and `consumed_at` moves from null to a timestamp once.

The local database is still mutable by an operator who can replace the file,
drop a trigger, or run arbitrary SQL. These constraints detect mistakes and
ordinary invalid writes; they are not cryptographic tamper evidence.

## Restart and concurrency evidence

The focused tests prove:

- idempotent and concurrent migration initialization;
- restart after issue followed by successful verification;
- another restart retaining the already-consumed replay state;
- 24 independent store instances producing one success and 23 replay failures;
- two issuers sharing a capacity of one producing one challenge and one bounded
  capacity failure;
- different configured capacities for one file failing initialization;
- exact expiration after restart;
- capacity cleanup of an expired row;
- shifted signed times not replacing durable issued facts;
- direct invalid nonce insertion being rejected; and
- issued facts being immutable under direct SQL update.

The 2026-08-30 clean committed snapshot at implementation commit
[`772d83c`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/772d83c)
passed 284/284 .NET tests, including 49/49 focused Authentication tests. It also
passed all 36 unchanged Foundry tests, the reviewed 1,030-byte/zero-slot Router
baseline, and isolated deployment plus signed Anvil lifecycle replay from 1,249
tracked files. The dynamic canary and working-tree/complete 38-commit history
scans found no leaks. GitHub Actions run
[`33300425392`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33300425392)
then independently passed locked .NET, Foundry plus signed RPC replay, and
secret-scan jobs.

## Residual limitations

Week 16 still has no:

- HTTP challenge or verification endpoint;
- binding between challenge delivery and one browser initiation context;
- secure cookie/session ID, rotation, logout, revocation, or CSRF control;
- user/tenant/role/authorization model;
- rate limit, IP/account abuse controls, or privacy-approved audit event;
- database backup, encryption, tamper evidence, or cross-host coordination;
- ERC-1271 contract-account verification; or
- public-network or production deployment.

All processes sharing one file must use the same reviewed path and capacity.
SQLite WAL is a local coordination mechanism, not a distributed consensus
system.

## Suggested reading order

1. `SiweChallengeDatabaseOptions.cs` for path and capacity bounds.
2. `SiweChallengeDatabaseMigrations.cs` for durable invariants.
3. `SiweChallengeDatabase.cs` for migration and connection ownership.
4. `SqliteSiweChallengeStore.cs` for immediate transactions and state changes.
5. `SqliteSiweChallengeStoreTests.cs` for restart, concurrency, and failure
   evidence.
6. The Week 15 guide for canonical parsing and ERC-191 recovery before storage.

## What should come next

Week 17 can add a bounded loopback HTTP authentication boundary. It must derive
the relying-party origin from trusted configuration, bind issuance to one
browser initiation context, use secure cookie semantics, define CSRF and logout,
and continue to keep SIWE authentication separate from payment authorization.
