# Week 7: Durable Payment Intent Persistence

Week 7 replaces the process-local dictionary from Week 6 with a migration-owned
SQLite database. The HTTP contract and Domain model do not change. The new
evidence is narrower and more useful: a created intent survives API restart,
and concurrent API processes that share one database file arbitrate the same
idempotency key through a database constraint.

Durability still does not imply payment. The database contains an off-chain
request, not a signature, transaction, chain observation, or settlement fact.

## Dependency boundary

Only `PaymentSandbox.Api` and its integration tests reference
`Microsoft.Data.Sqlite 10.0.11`. Domain remains independent of SQL and the
Contracts adapter remains independent of persistence.

```text
PaymentSandbox.Domain
          ^
          |
PaymentSandbox.Api -> Microsoft.Data.Sqlite -> local database file
```

The API keeps depending on `IPaymentIntentStore`. Replacing the implementation
therefore changes persistence behavior without changing endpoints or teaching
Domain about tables, transactions, or file paths.

## Configuration and startup

`appsettings.json` supplies the local default:

```json
{
  "PaymentIntents": {
    "DatabasePath": "data/payment-intents.db"
  }
}
```

A relative path is resolved once against the ASP.NET Core content root. The
resolved absolute path is then used for every connection, so a later working
directory change cannot silently select another database. Local `*.db`, WAL,
and shared-memory files are ignored by Git.

`PaymentIntentDatabaseInitializer` is an `IHostedService`. Kestrel does not
start accepting requests until `PaymentIntentDatabase.InitializeAsync` has
succeeded. An unavailable directory, malformed database, incompatible future
schema version, or failed migration therefore stops startup instead of running
against an unknown schema.

## Migration ownership

The application owns an append-only ordered list in
`PaymentIntentDatabaseMigrations`. Migration 1 creates:

```text
schema_migrations
  version INTEGER PRIMARY KEY
  name TEXT UNIQUE
  applied_at_utc TEXT

payment_intents
  payment_id TEXT PRIMARY KEY
  idempotency_key TEXT UNIQUE COLLATE BINARY
  chain_id TEXT
  token_address TEXT
  merchant_address TEXT
  amount_raw TEXT
  status TEXT CHECK status = 'created'
  created_at_utc TEXT
```

Both tables are SQLite `STRICT` tables. Additional `CHECK` constraints reject
obviously non-canonical IDs, addresses, decimal strings, zero values, and keys
outside the supported length. Application parsing remains the complete Domain
validation boundary; schema checks are defense in depth, not a replacement.

Migration creation, version inspection, SQL application, and migration-record
insertion share one serializable transaction. Concurrent initializers can run,
but the unique migration version is recorded once. An applied version with an
unexpected name or a version newer than this binary understands fails closed.
The migration table is not a cryptographic integrity mechanism and does not
make a locally modified database trustworthy.

## Insert first, then interpret

The idempotent create operation intentionally does not execute this sequence:

```text
SELECT key
  -> key absent
  -> INSERT row
```

Two processes can both observe absence before either inserts. Instead, the
store performs this parameterized statement inside a transaction:

```sql
INSERT INTO payment_intents (...)
VALUES (...)
ON CONFLICT(idempotency_key) DO NOTHING;
```

The database unique constraint chooses the winner. The application then maps
the result:

| Insert result | Follow-up | Store result |
| --- | --- | --- |
| One row inserted | Commit candidate | `Created` |
| Key conflict and normalized terms equal | Read original row, commit | `Replayed` |
| Key conflict and terms differ | Read only for comparison, commit | `Conflict` with no intent returned |

`COLLATE BINARY` preserves the existing case-sensitive key contract. The row
read after a conflict and the insert attempt share one transaction. A collision
on the independent random `payment_id` primary key still fails rather than
publishing inconsistent data.

## Connection and concurrency scope

Each operation opens and disposes its own pooled SQLite connection. A five-second
busy timeout gives a short concurrent writer time to finish instead of failing
immediately. WAL mode permits readers while another connection writes and is
stored as a database setting during initialization.

This supports multiple processes on one machine when they use the exact same
local database file. It is not horizontal scaling across copied files, remote
hosts, containers with separate volumes, or network file systems. Those cases
need a server database and an operational migration strategy.

## Test evidence

The Week 7 tests add these properties:

- initialize twice and concurrently, but record migration 1 exactly once;
- reject direct non-canonical writes through schema constraints;
- create through one store instance and query/replay through a new instance;
- stop Kestrel, restart against the same file, then query and safely replay;
- race 20 independent connections and observe exactly one create;
- keep case-sensitive idempotency keys distinct;
- return a non-leaking conflict for different terms;
- cancel before mutation without consuming the key;
- reject a database schema version newer than the application understands.

Every HTTP test uses its own random directory under the system temporary root.
Cleanup clears SQLite pools, validates the owned path prefix, and only then
recursively removes that exact directory. Tests never share production data.

## Run locally

```powershell
dotnet run --project .\src\PaymentSandbox.Api --urls http://127.0.0.1:5086
```

The default database is created under the API project's `data/` directory.
Override it without changing source:

```powershell
dotnet run --project .\src\PaymentSandbox.Api `
  --urls http://127.0.0.1:5086 `
  --PaymentIntents:DatabasePath C:\temp\payment-sandbox\intents.db
```

Use an absolute path for an operator-selected file. The database may contain
merchant addresses and business request history; do not commit, publish, or
treat it as encrypted.

## Residual limits and Week 8

SQLite closes restart loss for one configured file. It does not add backup,
encryption, tamper evidence, retention, deletion policy, tenant isolation,
authentication, authorization, rate limits, storage quotas, or cross-host
coordination. An unauthenticated client can still grow the database or create
lock pressure, so the API remains loopback/test-only.

Week 8 now adds a separate chain-observation schema and checkpoint boundary. It
keeps intent state separate from unfinalized logs: durable `created` still means
only that the off-chain request exists. Parent mismatch currently stops the
scan; common-ancestor recovery remains later work. See the [Week 8 observation
guide](week-08-chain-observation-checkpoints.md).
