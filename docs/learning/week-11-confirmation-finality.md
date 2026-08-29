# Week 11: Reversible confirmation-depth qualification

## Purpose

Week 10 records provisional effects and their reorg reversals. Week 11 adds a
separate answer to a narrower question:

> Under one named confirmation-depth policy, does an active provisional effect
> currently have enough blocks above it?

The output names are deliberately explicit:

```text
confirmation_qualified
confirmation_revoked
```

They do not say `settled`, `paid`, or economically irreversible. Confirmation
depth is a useful local policy signal, but a sufficiently deep reorganization,
an incomplete RPC view, or a dishonest provider can still invalidate it.

## Independent projection boundary

`PaymentSandbox.Finality` is another class library and another migration-owned
SQLite database:

```text
Domain <- Indexer <- Ledger <- Finality
```

Finality receives read-only interfaces. It cannot change the selected chain,
insert an observation, mutate a provisional Ledger entry, modify a Payment
Intent, or authorize a payout.

The database contains:

- copied append-only Ledger source entries;
- append-only qualification/revocation transitions; and
- a checkpoint binding every evaluation to exact Indexer and Ledger snapshots.

There is no mutable `is_final` column. Current qualification is derived from the
latest Finality transition for one Ledger effect generation.

## Policy identity

`ConfirmationFinalityPolicy` fixes:

- chain ID and Router;
- a readable policy ID;
- required confirmation count;
- maximum new Ledger entries per evaluation; and
- maximum total effects evaluated in one transaction.

The policy fingerprint includes identity and required confirmations, but not
resource limits. Operational limits may be tightened without changing the
meaning of existing decisions. Changing the policy ID or confirmation threshold
inside an existing projection stream fails closed; a different meaning needs a
separate stream/database or an explicit future migration.

Both readable policy fields and the fingerprint are stored in the checkpoint.
An evaluation that emits no transition is therefore still explainable later.

## Exact confirmation count

For an active effect in block `B` and selected Indexer head `H`:

```text
if H < B:
    confirmations = 0
else:
    confirmations = H - B + 1
```

The inclusion block counts as confirmation 1. With an effect in block 101:

| Head | Confirmation count |
| --- | ---: |
| 100 | 0 |
| 101 | 1 |
| 102 | 2 |
| 103 | 3 |

For a policy requiring 3 confirmations, block 103 is the first qualifying head.
The implementation saturates only the impossible edge where a signed 64-bit
height difference plus one cannot be represented; the qualification comparison
remains correct.

## Why one head number is not enough

Finality must not combine independently read values such as:

```text
old Indexer head + new canonicality cursor + lagging Ledger entries
```

That mixture could qualify an effect after the Indexer detached it but before
Ledger copied the reversal.

Week 11 adds `ChainObservationSnapshot`. One SQLite `SELECT` reads together:

- the stream checkpoint, including exact head number/hash/revision; and
- the global canonicality transition high-watermark.

Using one statement gives both values one SQLite read snapshot. The Finality
processor then requires:

```text
LedgerCheckpoint.LastSourceTransitionId
    == ChainObservationSnapshot.CanonicalityHighWatermark
```

A lower Ledger cursor may be missing a reversal. A higher cursor belongs to a
later Indexer snapshot than the caller selected. Both fail before Finality reads
or commits new source entries.

## The third cursor: Ledger entry high-watermark

Ledger entries also use a global append ID. Finality requires the caller-selected
`throughLedgerEntryId` to equal the current committed Ledger entry high-watermark.
This prevents silently checkpointing a prefix while a reversal already exists in
the unconsumed suffix.

IDs may have gaps for a chain/Router stream because another stream owns them.
Finality can checkpoint a global target even when its stream returned no new
entries, for the same reason Week 10 can checkpoint an empty transition interval:
future append IDs cannot appear below the committed global watermark.

The complete precondition is therefore:

```text
caller expected Indexer snapshot == current atomic Indexer snapshot
Ledger canonicality cursor       == snapshot transition watermark
caller Ledger entry target       == Ledger entry high-watermark
```

These reads cannot form a distributed transaction across three SQLite files.
They define an exact, fingerprinted snapshot boundary instead. A later commit in
Indexer or Ledger is a new fact and can cause a later revocation.

## Qualification state machine

Each canonical Ledger effect generation has its own Finality history:

```text
not qualified
  + active and confirmations >= threshold
      -> confirmation_qualified

qualified
  + Ledger effect reversed
      -> confirmation_revoked(reason = ledger_effect_reversed)

qualified
  + active but confirmations < threshold
      -> confirmation_revoked(reason = confirmation_threshold_lost)

revoked/not qualified
  + active and later reaches threshold again
      -> a new confirmation_qualified generation
```

A Ledger effect that was already reversed before the first Finality evaluation
never qualifies. Finality does not invent a historical qualification merely
because the effect was once canonical between two unobserved evaluation points.

Every revocation stores `revokes_transition_id`, which must reference the earlier
qualification for the same Ledger effect. Every qualification is revocable at
most once. A later requalification is a new row that can receive its own future
revocation.

## Deep reorganization example

Initial selected chain:

```text
100(1) -> 101(2)[payment P] -> 102(3) -> 103(4)
```

At head 103, payment P has 3 confirmations:

```text
Ledger entry 1: canonical_payment(P)
Finality transition 1: confirmation_qualified(entry 1, confirmations = 3)
```

The Indexer later replaces blocks 101-103:

```text
100(1) -> 101(e) -> 102(f) -> 103(9)
```

Indexer appends detach/attach transitions. Ledger first consumes those exact
transitions and appends a reversal for entry 1. Only after Ledger's canonicality
cursor equals the new Indexer snapshot may Finality evaluate:

```text
Ledger entry 2: canonical_payment_reversal -> entry 1
Finality transition 2: confirmation_revoked
                       -> transition 1
                       reason = ledger_effect_reversed
```

The old block, event, provisional effect, qualification, and both reversals all
remain queryable. Each layer adds an explanation; none rewrites history.

## Atomic Finality transaction

`SqliteFinalityStore.CommitAsync` performs:

```text
read finality checkpoint
  -> compare expected checkpoint or enter exact replay verification
  -> insert-or-verify newly consumed Ledger source entries
  -> enumerate active/inactive canonical effect generations with a hard limit
  -> derive required qualifications and revocations
  -> append decisions for the next finality revision
  -> revision-guard the checkpoint update
  -> commit
```

If effect enumeration exceeds policy, source inserts and decisions roll back
together. The cursor cannot advance while some effects remain unevaluated.

SQLite constraints and triggers independently require canonical encodings,
positive IDs/amounts, exact same-occurrence source reversals, qualification from
an active canonical effect, threshold-consistent decision counts, same-effect
revocation references, and one revocation per qualification.

## Fingerprints and retries

The evaluation batch fingerprint covers:

- the policy fingerprint;
- selected Ledger entry target;
- complete Ledger checkpoint identity;
- complete Indexer snapshot identity;
- every ordered new Ledger source entry and its exact payment facts.

The local Finality `RecordedAtUtc` is excluded. A retry after an unknown commit
may have a different local time but the same source facts. Exact source rows and
derived decisions are re-evaluated and verified before returning `Replayed`.
A changed amount, head hash, source cursor, policy, or other fact conflicts.

Two concurrent stores evaluating the same batch consequently produce one
`Applied` and one verified `Replayed` result.

## Failure behavior

| Condition | Result |
| --- | --- |
| same explicit head and Ledger target already evaluated | `NoWork`; sources are not read |
| expected Indexer snapshot changed | fail before Ledger reads |
| Ledger canonicality cursor behind/ahead of snapshot | fail before entry reads |
| selected Ledger target differs from committed high-watermark | fail before entry reads |
| new Ledger entry limit exceeded | fail before Finality commit |
| total effect limit exceeded | transaction rollback |
| policy meaning differs from durable checkpoint | fail closed |
| changed source fact for an already committed batch | checkpoint conflict |
| caller cancellation | cancellation propagates without cursor advance |

## Verification evidence

The focused suite covers strict/idempotent migration, threshold boundaries,
head regression, requalification, Ledger reversal, pre-reversed effects, unknown
outcome replay, changed-source conflict, concurrent commits, policy immutability,
resource-limit rollback, exact target guards, source catch-up, and no-work reads.

The three-database integration test builds real Indexer, Ledger, and Finality
stores. It qualifies a payment at three confirmations, replaces its three-block
suffix, consumes the Ledger reversal, and proves Finality appends a linked
`ledger_effect_reversed` revocation while all earlier evidence remains present.

The 2026-08-30 committed-snapshot verification passed 171/171 .NET tests. The
focused counts were Indexer 40/40, Ledger 20/20, and Finality 18/18. The same run
passed all 36 unchanged Foundry tests, the reviewed 1,030-byte/zero-storage-slot
Router baseline, and successful/reverted Anvil observation replay from 1,152
Git-known files. The dynamic secret canary, candidate working-tree scan, and
complete 23-commit history scan found no leaks. Implementation commit
[`f8c8a69`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/f8c8a69)
records the boundary verified here. GitHub Actions run
[`33270360367`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33270360367)
then passed all three jobs: locked .NET build/tests, Foundry build/tests/RPC
observation, and working-rules/full-history secret scan.

## What remains for Week 12 and later

Week 11 does not add:

- protocol-native finalized/safe block tags or chain-specific consensus proofs;
- trusted anchors, independent providers, or log-completeness evidence;
- token `Transfer` and balance-delta verification;
- Payment Intent matching;
- merchant balances, accounting journals, payout authorization, or custody;
- reconciliation and discrepancy records;
- a hosted worker, scheduler, retention, backup, or tamper evidence.

Week 12 should reconcile independently sourced facts and explain mismatches. It
must consume qualification as one policy input, not redefine it as settlement.

## Suggested reading order

1. `ChainObservationSnapshot.cs` and `IChainObservationReader.cs`.
2. `ILedgerEntryReader.cs` and the bounded SQLite entry query.
3. `ConfirmationFinalityPolicy.cs` for meaning versus resource limits.
4. `FinalityEvaluationBatch.cs` for cross-source validation and fingerprinting.
5. `ConfirmationFinalityProcessor.cs` for source catch-up order.
6. `FinalityDatabaseMigrations.cs` for durable constraints and triggers.
7. `SqliteFinalityStore.cs` for state derivation, replay, and atomic commit.
8. Finality persistence, processor, and deep-reorg integration tests.
