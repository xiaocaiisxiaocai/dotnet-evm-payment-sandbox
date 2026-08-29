# Week 8: bounded chain observations and durable checkpoints

Week 8 introduces `PaymentSandbox.Indexer`, but not a continuously running
indexer service. The deliverable is a smaller reusable batch boundary: read an
explicit block range from one configured chain and Router, validate the returned
block/log relationships, and atomically store the observations with a restart
checkpoint.

The checkpoint means only "this range was scanned and durably recorded." It
does not mean its last block is final, permanently canonical, or safe to credit.
No Payment Intent status, ledger balance, or settlement decision changes here.

## Dependency and responsibility boundary

```text
PaymentSandbox.Domain
  ^              ^
  |              |
Contracts     Indexer
  ^              |
  | reviewed     +-- Nethereum exact-range RPC adapter
  | event DTO    +-- validation processor
  +--------------+-- SQLite observation/checkpoint store
```

The Indexer references Domain for exact EVM values and Contracts for the
reviewed `PaymentRecordedEventDto`. Domain still knows nothing about RPC,
Nethereum, blocks, or SQLite. The API still does not reference the Indexer;
creating an offline Intent cannot silently trigger network work.

There is deliberately no hosted worker or default RPC URL. A later composition
root must supply an operator-reviewed chain, Router, start block, endpoint,
database path, range target, and lifecycle policy.

## One scan in execution order

`ChainObservationProcessor.ScanThroughAsync(target)` performs these steps:

1. Read the durable checkpoint for `(chainId, router)`.
2. Derive the first unscanned block, or use the configured start block.
3. Return `NoWork` without contacting RPC when the target is already covered.
4. Reject a range above the configured block limit.
5. Call `eth_chainId` and fail if it differs from policy.
6. Read every exact block number and validate its number, hash, and parent link.
7. Query `PaymentRecorded` logs for that exact inclusive range.
8. Reject an excessive log count before decoding or persistence.
9. Validate each event's emitter, block number/hash, occurrence identity, and
   typed event fields.
10. Commit blocks, payments, and the new checkpoint in one SQLite transaction.

The caller selects an exact target; the interface contains no implicit
"scan latest forever" operation. This makes one batch deterministic enough to
test and prevents a moving head from being hidden inside the adapter.

## Raw RPC data is not trusted model data

The RPC interface returns deliberately raw shapes:

- `RpcBlockHeader` carries a reported number, hash, and parent hash.
- `RpcPaymentRecordedLog` carries emitter, block/transaction occurrence fields,
  `removed`, and decoded ABI values.

The processor then creates validated models:

- `EvmHash` normalizes one 32-byte hash; unlike `PaymentId`, it permits zero so
  a genesis parent can be represented.
- `ObservedBlock` ties one height to exact block and parent hashes.
- `PaymentRecordedObservation` requires canonical chain-aware fields, non-zero
  addresses, a non-zero PaymentId, and a positive uint256 amount.
- `ChainObservationCheckpoint` records the stream start, last height/hash,
  monotonic revision, and local update time.

This separation is important. Successfully deserializing JSON or ABI data does
not prove that an endpoint told the truth or that the observation is canonical.
It only gives validation code a structured input to check.

## Event occurrence identity

Repeated `PaymentId` values remain valid. The store therefore does not use
PaymentId as an event primary key. One observed occurrence is identified by:

```text
chainId + router + blockHash + transactionHash + logIndex
```

Including block hash matters because the same transaction may be re-included in
a different block after a reorg. Week 8 preserves distinct observations instead
of forcing one row per PaymentId or transaction hash. Later canonicality and
reversal logic can reason about them without losing history.

## Parent continuity and the current reorg boundary

Within a batch, every block must point to the preceding block hash. The first
block of a later batch must point to the checkpoint's last block hash. A mismatch
causes the batch to fail without moving the cursor.

This detects an important class of reorg/stale-provider problems, but it does not
recover from them. Week 8 does not:

- search backward for a common ancestor;
- mark a previously observed fork non-canonical;
- produce append-only reversals;
- compare independent providers;
- define confirmations or economic finality.

Those require explicit history and accounting semantics in later weeks. Silently
overwriting the old block or moving the cursor backward here would erase the
evidence needed to implement them correctly.

## SQLite schema ownership

The Indexer uses a separate database boundary with its own migration history.
Migration 1 creates:

| Table | Responsibility |
| --- | --- |
| `schema_migrations` | Ordered application-owned schema versions |
| `indexer_checkpoints` | One active restart cursor per chain/Router stream |
| `observed_blocks` | Append-only block identities, including competing hashes at one height |
| `payment_recorded_observations` | Append-only decoded event occurrences linked to observed blocks |

The tables are SQLite `STRICT` tables with canonical decimal/hex checks,
non-zero address/PaymentId checks, uint256 amount bounds, occurrence keys, and a
foreign key from each payment observation to its exact observed block.

Indexer migrations fail closed on unknown future versions or mismatched known
names. WAL mode, foreign keys, a five-second busy timeout, short-lived pooled
connections, and parameterized commands match the local durability discipline
introduced for Payment Intents without combining the two ownership boundaries.

## Atomic commit and unknown outcomes

`SqliteChainObservationStore.CommitBatchAsync` first validates the batch as one
contiguous stream. Inside one serializable transaction it then:

```text
read current checkpoint
  -> compare with caller's expected previous checkpoint
  -> insert or verify every observed block
  -> insert or verify every payment occurrence
  -> insert/update checkpoint with a revision guard
  -> commit
```

Blocks or events cannot become durable while the checkpoint remains behind, and
the checkpoint cannot advance without their rows. Two scanners racing the same
range produce one `Applied` result and one verified `Replayed` result.

The replay path also handles a lost commit response. It does not blindly accept
`ON CONFLICT DO NOTHING`: the resulting checkpoint position and every source
field are compared with the retry. Local observation timestamps may differ and
do not change chain identity. A different block, event, revision, or cursor is a
`CheckpointConflictException`, not a successful retry.

## Bounded failure behavior

| Input or state | Result |
| --- | --- |
| Target already at/below checkpoint | `NoWork`, no RPC call |
| Range exceeds configured block limit | Reject before RPC |
| RPC chain ID differs | Reject before blocks/logs |
| RPC error or cancellation | Wrapped failure with cause, or unchanged cancellation |
| Missing/wrong-number/malformed block | Reject; no persistence |
| Parent hash mismatch | Reject; checkpoint unchanged |
| Too many logs | Reject before decoding/persistence |
| Removed, out-of-range, wrong-emitter, or wrong-block log | Reject; checkpoint unchanged |
| Duplicate occurrence in one response | Reject |
| Same batch after unknown commit | Verify rows and return `Replayed` |
| Concurrent different cursor | Reject as checkpoint conflict |
| Unknown newer database schema | Fail initialization |

## Protocol-level test evidence

Most processor tests use an in-memory RPC fake and a real temporary SQLite file
so failures can assert that no rows or cursor leaked through. A separate loopback
Kestrel fixture exercises Nethereum itself: it captures `eth_chainId`,
`eth_getBlockByNumber`, and `eth_getLogs`, returns raw ABI topics/data for the
reviewed `PaymentRecorded` event, and checks the adapter's decoded fields.

The focused Week 8 suite currently has 33 tests covering values, policy limits,
RPC protocol mapping, migrations, schema constraints, restart, atomic replay,
concurrent scanners, exact maximum block arithmetic, malformed observations,
and parent mismatch behavior. It uses no external endpoint, key, or wallet.

The supported full repository entry point on 2026-08-30 passed 126/126 .NET
tests and all 36 unchanged Foundry tests. It also rechecked the reviewed Router
ABI/runtime/storage baseline, replayed successful and reverted Anvil evidence
from 1,097 Git-known files, proved the dynamic Gitleaks canary, and found no
secret in the candidate tree or complete 15-commit history.

## What the database still cannot prove

A stored row proves that this application accepted one endpoint's response at a
time. It does not independently prove:

- the endpoint was honest or complete;
- the Router address/code policy was revalidated at a trusted block;
- the block remains canonical;
- the token delivered the event's requested amount;
- the event matches a known Payment Intent;
- confirmations or finality were reached;
- a merchant should be credited.

The observation database is local, mutable, unencrypted application data. It has
no backup, tamper evidence, retention policy, or cross-host coordination.

## Suggested reading order

1. `ChainObservationPolicy.cs` for stream identity and resource limits.
2. `IChainObservationRpc.cs` and raw RPC records for the trust boundary.
3. `NethereumChainObservationRpc.cs` for exact RPC and ABI mapping.
4. `EvmHash`, `ObservedBlock`, and `PaymentRecordedObservation` for validated
   occurrence identity.
5. `ChainObservationProcessor.cs` for ordered fail-closed validation.
6. `IndexerDatabaseMigrations.cs` for durable constraints and append-only keys.
7. `SqliteChainObservationStore.cs` for atomic commit/replay behavior.
8. Processor, persistence, and protocol tests to read each failure backward.

Week 9 can build common-ancestor detection and explicit fork handling on these
preserved observations. It must keep canonicality/finality policy separate from
the fact that an endpoint once returned a block or log.
