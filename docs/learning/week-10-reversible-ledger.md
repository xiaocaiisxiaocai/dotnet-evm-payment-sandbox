# Week 10: Append-only provisional effects and reversals

## Purpose

Week 9 records how the local Indexer changes its selected branch, but it does
not express the downstream consequence of that change. Week 10 adds one narrow
projection:

```text
canonical block + PaymentRecorded occurrence
    -> append provisional canonical_payment effect

later noncanonical transition for that exact occurrence
    -> append canonical_payment_reversal linked to the active effect
```

The words **provisional** and **local** are essential. This layer does not add
confirmation depth, chain-specific finality, a merchant balance, a payout
decision, token `Transfer` verification, or reconciliation with Payment Intents.

## Why this is a separate project and database

`PaymentSandbox.Indexer` owns source evidence: exact blocks, event occurrences,
canonicality transitions, and its scan checkpoint. `PaymentSandbox.Ledger` owns
derived effects and its own source cursor. Keeping those concerns separate gives
each database one migration owner and prevents a ledger rule from rewriting an
observation fact.

The dependency direction is:

```text
Domain <- Indexer <- Ledger
```

Ledger sees Indexer through `IChainObservationReader`, a read-only boundary. It
cannot switch the selected chain, insert observations, or advance the Indexer
checkpoint.

## The source cursor is explicit

`ProcessThroughTransitionAsync(throughTransitionId)` never asks for an implicit
`latest` loop. The caller chooses a committed source high-watermark, and the
processor verifies that the target does not exceed the largest transition ID
currently committed by the Indexer.

Transition IDs are global to one Indexer database, not dense within one
`(chainId, router)` stream. Therefore a stream may legitimately read no rows for
IDs 1 through 9 and still checkpoint source ID 9: those IDs may belong to other
streams. A later row for this stream cannot appear below that cursor because
SQLite allocates new append IDs above the existing high-watermark.

The processor has separate limits for:

- source transitions per batch; and
- payment occurrences across those transitions.

Each read asks for `limit + 1`. The extra row is only a lookahead that proves the
selected interval exceeds policy; it is never silently dropped and checkpointed.

## Exact occurrence identity

A ledger entry is keyed to the same occurrence identity as its observation:

```text
(chainId, router, blockHash, transactionHash, logIndex)
```

Block hash is required. Height alone would confuse two forks at the same block
number. `PaymentId` remains correlation data: two distinct occurrences can use
the same PaymentId and must not collapse into one ledger entry.

For each canonicality transition, the reader fetches payments from the exact
`(blockNumber, blockHash)` occurrence. `CanonicalPaymentChange` snapshots the
collection, rejects a payment from another block or stream, and rejects duplicate
transaction/log identities before persistence begins.

## Entry generations

Ledger history is a small state machine per exact payment occurrence:

```text
no active effect
  -- canonical --> effect generation 1 active
  -- noncanonical --> invalid

effect generation 1 active
  -- canonical --> invalid duplicate effect
  -- noncanonical --> reversal -> no active effect

no active effect after reversal
  -- canonical --> effect generation 2 active
```

An effect is never updated to `reversed` and never deleted. The reversal row
stores `reverses_entry_id`, which points to the exact earlier active generation.
If the old fork becomes canonical again, a new effect row is appended. This
preserves the full causal sequence instead of reducing it to a mutable boolean.

## Database invariants

Migration 1 creates:

- `ledger_checkpoints`, one source cursor per chain/Router stream;
- `canonical_payment_ledger_entries`, the append-only effect/reversal history;
- one unique source-transition/occurrence identity;
- one partial unique index allowing at most one reversal per effect entry; and
- a trigger requiring a reversal to reference an earlier canonical effect for
  the same chain, Router, block hash, transaction hash, and log index.

The schema is `STRICT` and constrains canonical decimal/hex encodings, non-zero
identifiers and addresses, positive `uint256` amounts, entry kinds, and reversal
nullability. Foreign keys use `ON DELETE RESTRICT`. Application code additionally
checks that only one unreversed effect generation is active.

These controls make accidental corruption harder; they are not tamper evidence.
Someone who can replace or deliberately rewrite the local SQLite file remains
inside the trust boundary.

## One atomic commit

`SqliteLedgerStore.CommitAsync` performs this sequence in one serializable
transaction:

```text
read durable ledger checkpoint
  -> compare with caller's expected checkpoint
  -> for each ordered source change and exact payment:
       find the active effect before this transition
       validate canonical/reversal state transition
       insert or verify the derived entry
  -> revision-guard insert/update of source checkpoint
  -> commit
```

The source observation database and ledger database cannot share one SQLite
transaction. Cross-database exactly-once behavior is therefore obtained by a
durable consumer checkpoint plus deterministic source facts. A crash before the
ledger transaction commits leaves neither entries nor cursor. A crash after
commit but before the caller receives the result is handled as an exact replay.

## Batch fingerprints and unknown outcomes

Every batch computes a SHA-256 fingerprint over a versioned domain prefix, stream,
target cursor, ordered transitions, and ordered payment facts. Length-prefixed
strings and big-endian integers make the byte representation unambiguous.

`RecordedAtUtc` is intentionally excluded. It is local bookkeeping, not a source
fact, and a retry after an unknown commit will naturally have a different local
time. `sourceChangedAtUtc`, canonicality, reason, checkpoint revision, block
identity, occurrence identity, addresses, PaymentId, and amount are included.

When the durable checkpoint already equals the proposed target and revision:

- matching fingerprint and matching derived entries return `Replayed`;
- any changed source fact produces a different fingerprint and fails as a
  `LedgerCheckpointConflictException`;
- a missing or changed derived row fails verification instead of being accepted.

Two concurrent writers of the same batch consequently produce one `Applied`
and one verified `Replayed` result.

## Reorganization example

Suppose the Indexer first selects block `101(0x22...)` containing payment `P`:

```text
transition 2: canonical 101(0x22...) -> ledger entry 1 canonical_payment(P)
```

It then switches to block `101(0xee...)`, which contains payment `Q`:

```text
transition 3: noncanonical 101(0x22...)
    -> ledger entry 2 canonical_payment_reversal(P), reverses entry 1

transition 4: canonical 101(0xee...)
    -> ledger entry 3 canonical_payment(Q)
```

Both Indexer payment occurrences remain stored. Ledger entries 1 and 2 remain
stored. The current provisional interpretation can be derived, while the reason
for every change is still auditable.

## Failure behavior

| Condition | Result |
| --- | --- |
| target already checkpointed | `NoWork`; source is not read |
| target above source high-watermark | fail before transition/payment reads |
| transition or payment limit exceeded | fail before ledger commit |
| source returns another stream/range | fail as invalid source data |
| reversal has no active effect | whole ledger transaction rolls back |
| canonical change overlaps an active effect | whole transaction rolls back |
| stale different checkpoint or source fingerprint | checkpoint conflict |
| caller cancellation | cancellation propagates; it is not a successful cursor advance |
| source exception | wrapped with the failing read-boundary context |

## Verification evidence

The focused Ledger suite includes migration, strict-schema, concurrent
initialization, apply/reversal/re-canonicalization, rollback, exact replay,
source-fact conflict, concurrent commit, resource limits, empty global-ID gaps,
and source failure tests.

The cross-boundary test uses real Indexer and Ledger SQLite databases. It commits
an initial fork, projects its payment, atomically switches the Indexer to a
replacement fork, projects the new high-watermark, and proves:

- the old payment has one effect plus one linked reversal;
- the replacement payment has one effect;
- both fork observations remain queryable in the source database; and
- the ledger checkpoint reaches the exact committed transition high-watermark.

The 2026-08-30 full local entry point passed 152/152 .NET tests (40 focused
Indexer and 19 focused Ledger), 36/36 unchanged Foundry tests, the reviewed
1,030-byte/zero-storage Router baseline, and successful/reverted Anvil replay
from 1,126 Git-known files. The dynamic secret canary and scans of the candidate
working tree plus complete 20-commit history also passed. Implementation commit
[`60643db`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/60643db)
records the tested Week 10 boundary.

## What remains for Week 11 and later

Week 10 deliberately does not add:

- confirmation depth or chain-specific finality states;
- trusted block anchors, independent RPC comparison, or log-completeness proof;
- token `Transfer`/balance-delta evidence for unusual tokens;
- merchant balances, double-entry accounts, payout authorization, or custody;
- matching between Payment Intents and one or more payment occurrences;
- reconciliation and explainable discrepancy records;
- a hosted worker, scheduler, retention policy, backup, or tamper evidence.

Week 11 should introduce finality as another explicit policy/projection. It must
not rename provisional `canonical_payment` entries to “settled” or erase their
reversal history.

## Suggested reading order

1. `IChainObservationReader.cs` for the read-only source contract.
2. `BlockCanonicalityTransition.cs` for the source fact model.
3. `CanonicalPaymentChange.cs` and `CanonicalPaymentBatch.cs` for validation and
   deterministic fingerprints.
4. `CanonicalPaymentLedgerProcessor.cs` for cursor and resource-limit behavior.
5. `LedgerDatabaseMigrations.cs` for durable database invariants.
6. `SqliteLedgerStore.cs` for state transitions, replay verification, and the
   atomic checkpoint transaction.
7. Ledger persistence, processing, and cross-database integration tests for the
   executable failure and reorganization examples.
