# Week 9: Bounded reorg recovery and canonicality history

## Goal and boundary

Week 8 could detect that a new block did not extend the durable checkpoint, but
it intentionally stopped there. Week 9 adds the smallest recovery mechanism that
can explain and switch forks without destroying evidence:

- search backward for a common ancestor within an operator-selected limit;
- read and validate one complete replacement suffix;
- retain blocks and events from the detached fork;
- append explicit canonical/noncanonical transitions;
- switch those transitions and the checkpoint in one SQLite transaction.

This is still observation infrastructure. `canonical` means "the branch this
local observer currently selects from one RPC view." It does not mean confirmed,
finalized, complete, settled, or safe to credit.

## Why source facts and current interpretation are separate

An occurrence such as `(height, blockHash)` is a historical fact: the observer
accepted that exact block from its endpoint at a particular time. A later reorg
does not make the observation disappear. It changes only whether the occurrence
belongs to the observer's current branch.

For that reason Week 9 does not add a mutable `canonical` column to
`observed_blocks`, and it never deletes old `payment_recorded_observations`.
Migration 2 creates `block_canonicality_transitions` instead. Each row records:

| Field | Meaning |
| --- | --- |
| stream identity | exact chain ID and Router address |
| block identity | exact height and block hash |
| checkpoint revision | atomic branch-selection operation that caused the change |
| canonicality | `canonical` or `noncanonical` |
| reason | normal observation, detached fork, replacement fork, or Week 9 backfill |
| timestamp | local time at which the interpretation changed |

The current canonical occurrence at a height is derived from the latest
transition for each exact occurrence. This permits a later reorg to select a
previously detached occurrence again while preserving the complete history.

## Migration 2 and Week 8 databases

Before Week 9, the public store could only append one parent-linked chain. An
existing Week 8 database can therefore seed every stored block as canonical at
its current checkpoint revision. Migration 2 performs that backfill in the same
owned migration transaction that creates the table and index.

Fresh databases apply migrations 1 and 2 in order. Existing databases apply only
the missing migration. Repeated and concurrent initialization remains safe, and
an unknown newer schema or a known version with another owner name still fails
closed.

## The processor recovery algorithm

`ScanThroughAsync(target)` first follows the normal Week 8 path. Recovery starts
only when all of these facts are true:

1. a durable checkpoint already exists;
2. the first block after that checkpoint has the wrong parent hash;
3. chain identity and the original forward-range limit already passed.

A parent mismatch later inside the freshly read range is different. It means the
single RPC read is internally inconsistent or changed while being observed. The
processor throws `ChainParentMismatchException` and does not guess a fork.

For a boundary mismatch, `FindCommonAncestorAsync` works backward from the old
tip. At each height it reads:

```text
durable block selected by latest canonicality transition
                         versus
exact RPC block requested by that same block number
```

The first exact `ObservedBlock` match is the common ancestor. Search stops and
fails when it reaches either the configured stream start or `MaxReorgDepth`
without a match. Missing durable or RPC blocks also fail. No source row,
transition, or checkpoint is written during this search.

After finding ancestor `A`, the processor re-reads every block in
`[A + 1, target]`. The first replacement block must extend `A`; every later block
must extend its immediate predecessor. Logs are fetched for exactly the same
replacement range and pass all Week 8 emitter, block, occurrence, address,
PaymentId, amount, duplicate, removed, and count checks.

The original forward work is bounded by `MaxBatchSize`; detached history is
bounded by `MaxReorgDepth`. The largest recovery read is therefore bounded by
their sum. There is still no implicit `latest` query or endless polling loop.

## The atomic reorg transaction

`CommitReorganizationAsync` receives the old checkpoint, proven common ancestor,
and validated replacement batch. Inside one serializable transaction it:

1. re-reads and compares the current checkpoint with the caller's expectation;
2. proves that the selected ancestor is still locally canonical;
3. reads the entire old canonical suffix and verifies height/parent continuity;
4. insert-or-verifies every replacement block and event source row;
5. appends `noncanonical/reorg_detached` transitions for the old suffix;
6. appends `canonical/reorg_replacement` transitions for the new suffix;
7. revision-guards the checkpoint update to the replacement tip;
8. commits all changes together.

Any exception rolls the whole transaction back. Old block and event rows are
never updated or deleted.

## Concurrency and unknown outcomes

Two scanners may discover the same fork concurrently, or SQLite may commit while
the caller loses the response. The second call is not accepted merely because
the checkpoint now resembles its desired tip. It must verify:

- every replacement block source field;
- every replacement event source field;
- every replacement canonical transition and reason;
- the complete detached transition chain, including its old checkpoint tip;
- the expected next checkpoint revision.

Only that exact result returns `Replayed`. A different tip, revision, row, or
transition is a conflict. Thus a concurrent identical reorg has one
`Reorganized` result and one verified `Replayed` result.

## Result meanings

| Result | Meaning |
| --- | --- |
| `Applied` | A normal forward suffix and its canonical transitions committed |
| `Reorganized` | A validated old suffix was detached and replacement committed |
| `Replayed` | The exact normal or reorg result was already durable and verified |
| `NoWork` | The caller-selected target was already covered |

`DetachedBlockCount` is diagnostic evidence about the local branch switch. It is
not a ledger reversal count.

## Failure behavior

| Condition | Durable result |
| --- | --- |
| Boundary mismatch with common ancestor in range | Atomic detach/attach and checkpoint switch |
| Common ancestor deeper than limit | Reject; old checkpoint and rows unchanged |
| Configured first block differs | Reject; no earlier stored proof exists |
| Missing durable canonical block | Reject as local inconsistency |
| Missing or malformed RPC ancestor block | Reject |
| Parent mismatch inside replacement suffix | Reject; do not recursively recover |
| Invalid or excessive replacement logs | Reject |
| Checkpoint changes before commit | Conflict or exact verified replay |
| Any transaction write fails | Roll back transitions, source rows, and checkpoint |

## Test evidence

The focused Indexer suite has 39 tests. Week 9 additions cover:

- Migration 2 backfill from a reconstructed Week 8 database;
- concurrent, idempotent schema initialization;
- a multi-block synthetic reorg with both forks retained;
- current canonical queries selecting the replacement fork;
- old-fork event occurrences remaining present;
- continuation with a normal batch after a reorg;
- a reorg beyond `MaxReorgDepth` leaving durable state unchanged;
- an internal new-range parent mismatch failing instead of invoking recovery;
- concurrent identical reorg commits producing one apply and one replay;
- rejection of a direct store call that selects a non-highest ancestor.

The tests use fake RPC data for precise fork control and real temporary SQLite
files for constraints, transactions, concurrency, migration, and restart
behavior. The existing loopback raw JSON-RPC/ABI test remains unchanged.

The supported full verification entry point passed 132/132 .NET tests and all
36 unchanged Foundry tests. It also preserved the reviewed Router runtime size,
Keccak, and empty storage layout; replayed successful and reverted Anvil evidence
from 1,097 Git-known files; proved the dynamic secret canary; and found no secret
in the working tree or complete 17-commit history.

## What remains for later weeks

Week 9 does not add:

- confirmation depth or chain-specific finality;
- independent providers, trusted block anchors, or log-completeness proof;
- a ledger entry for a canonical event;
- compensating ledger reversals for a detached event;
- reconciliation between intents, observations, balances, and ledger effects;
- a hosted worker, scheduling, public RPC configuration, or production storage.

Week 10 should consume canonicality changes through a separate append-only ledger
boundary. It must not mutate observation facts or treat a local canonical label as
final settlement.

## Suggested reading order

1. `ChainObservationPolicy.cs` for the independent batch/log/reorg limits.
2. `ChainParentMismatchException.cs` for safe boundary-versus-internal routing.
3. `ChainObservationProcessor.cs` for common-ancestor and replacement reads.
4. `IndexerDatabaseMigrations.cs` for transition schema and Week 8 backfill.
5. `IChainObservationStore.cs` for the normal/reorg atomic contracts.
6. `SqliteChainObservationStore.cs` for current-state queries and transactions.
7. Processor and persistence tests for each success, retry, and failure path.
