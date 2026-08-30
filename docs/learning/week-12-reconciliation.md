# Week 12: explainable payment reconciliation

## Purpose and boundary

Week 12 compares facts that earlier weeks intentionally kept separate:

- an immutable off-chain Payment Intent;
- append-only provisional Ledger effects and reversals; and
- append-only confirmation qualification and revocation decisions.

`PaymentSandbox.Reconciliation` does not change any of those sources. It writes
an immutable report explaining whether they agree for one explicit `PaymentId`
at exact source watermarks.

Even a report with `IsConsistent = true` means only that these local facts agree.
It does not prove an RPC was honest or complete, a token delivered its requested
amount, consensus finalized the block, an accounting journal was posted, or a
payout may proceed.

## Why reconciliation is a separate project and database

Updating `PaymentIntent.Status` to `paid` would collapse several independent
questions into one mutable flag:

1. Was an occurrence observed on the selected local branch?
2. Does its chain, token, and merchant match the Intent?
3. Do all active matching occurrence amounts aggregate to the requested amount?
4. Which matching effects currently satisfy the named confirmation policy?
5. Was an earlier effect later reversed?

The new project instead references read-only source interfaces. Its own SQLite
file has an independent migration history and contains only appended reports and
the exact selected evidence copied into each report.

## Three atomic source snapshots

### Intent publication snapshot

An existing Intent is immutable, but absence also needs a cursor. Without one,
the processor could read “missing,” then an Intent could be created immediately,
leaving no way to identify the selected past state.

Payment Intent migration 2 adds `payment_intent_publications`:

```text
publication_id -> payment_id
```

Existing version-1 rows are backfilled deterministically. An insert trigger
publishes every later Intent in the same SQLite transaction as its resource row.
`GetSnapshotAsync(paymentId)` reads the optional Intent, its publication ID, and
the global publication high-watermark in one SQL statement.

### Ledger snapshot

`LedgerReadSnapshot` atomically pairs one stream checkpoint with the database's
global entry high-watermark. `GetEntriesByPaymentIdAsync` then reads only rows
for the selected chain/Router/payment ID through that immutable watermark.
Global IDs may have gaps because other streams can own intervening rows.

### Finality snapshot

`FinalityReadSnapshot` atomically pairs one stream checkpoint with the database's
global Finality transition high-watermark. Each selected Ledger effect is read
through that watermark and bounded by a per-effect transition limit.

Later appends cannot change any selected prefix. This makes separate read
statements safe without pretending the independent SQLite files share a
distributed transaction.

## Exact catch-up rules

Before classification, all current source snapshots must equal the caller's
explicit expected snapshots. The evaluation then requires:

```text
Finality.LastLedgerEntryId
  == Ledger.EntryHighWatermark

Finality.LedgerCheckpointRevision
  == Ledger.Checkpoint.Revision

Finality.LastIndexerTransitionId
  == Ledger.Checkpoint.LastSourceTransitionId
```

A lower Finality cursor may omit a known Ledger reversal. A different Ledger
revision may describe another source batch even when no payment row was added.
Both cases fail closed before a report is committed.

The source interfaces are also explicit trust boundaries. Reconciliation does
not assume that every future `ILedgerEntryReader` or `IFinalityReader`
implementation is correct merely because the current SQLite implementation is.
It independently rejects:

- unknown entry, transition, or reason enum values;
- unordered or out-of-watermark rows;
- canonical effects carrying reversal references;
- reversals that do not exactly match one active earlier occurrence;
- duplicate qualifications or revocations that do not close the active
  qualification generation; and
- reversed effects that still retain an unrevoked qualification after Finality
  claims exact catch-up; and
- confirmation counts or required thresholds inconsistent with the selected
  effect and Finality checkpoint.

This validation is intentionally repeated downstream. It prevents a malformed
reader response from becoming durable Reconciliation evidence.

## Occurrence state is derived, not stored

For every `canonical_payment` effect selected for the payment ID:

- it is active when no selected `canonical_payment_reversal` references it;
- its latest selected Finality transition determines whether it is currently
  `confirmation_qualified`; and
- a later qualification after revocation belongs to that same effect's newer
  policy-decision generation.

The Reconciliation database does not store a mutable active or qualified flag.
The evaluation derives both from complete append-only histories.

## Matching and aggregation rules

An active effect contributes to the matching amount only when:

```text
Intent.chainId == reconciliation policy chainId
effect.token == Intent.token
effect.merchant == Intent.merchant
```

Payer is deliberately not part of Intent terms. The Router permits any payer to
pay a public correlation ID.

All compatible active occurrences aggregate with `BigInteger`, not `uint256`.
Each individual event is a `uint256`, but repeated events can sum above that
range and the comparison must not overflow.

This makes partial and supplemental payments first-class:

```text
500000 + 750000 == Intent 1250000
```

Repeated exact or excess transfers remain visible and produce an overpayment
discrepancy instead of being silently deduplicated.

## Stable discrepancy codes

| Code | Meaning at the selected snapshot |
| --- | --- |
| `intent_missing` | no Intent existed through the selected publication watermark |
| `active_payment_missing` | an Intent exists but no active occurrence remains |
| `reversed_payment_history` | old effects exist but every selected generation is reversed |
| `chain_mismatch` | the Intent names another chain than the reconciliation stream |
| `token_mismatch` | at least one active occurrence uses another token |
| `merchant_mismatch` | at least one active occurrence names another merchant |
| `amount_underpaid` | compatible active amount is below the Intent amount |
| `amount_overpaid` | compatible active amount exceeds the Intent amount |
| `qualification_incomplete` | some compatible active amount is not currently qualified |

Several codes can coexist. A reversed payment can simultaneously be missing,
underpaid, and have explicit reversal history. That is more informative than
choosing one headline status and discarding the other dimensions.

When no Intent exists, the report records `intent_missing` without inventing
expected token, merchant, or amount terms.

## Meaning of `IsConsistent`

`IsConsistent` is derived as `Discrepancies.Count == 0`. Therefore it requires:

- an Intent;
- no active term mismatch;
- compatible active amount exactly equal to the requested amount; and
- the entire compatible active amount currently qualified.

It is not persisted as an instruction to settle. It is a compact summary of the
same report whose counts, amounts, codes, source coordinates, and evidence rows
remain available for inspection.

## Durable report transaction

One serializable transaction writes:

```text
reconciliation_reports
  + reconciliation_report_ledger_entries
  + reconciliation_report_finality_transitions
  + reconciliation_report_discrepancies
```

The report stores source watermarks, policy identities, occurrence counts,
matching/qualified amounts, the consistency summary, and a deterministic batch
fingerprint. Child tables copy every selected Ledger and Finality field needed
to explain and strictly replay the decision.

The unique source key contains the payment ID, reconciliation policy fingerprint,
and all selected source coordinates. A later source watermark appends a new
report rather than updating the old one.

## Fingerprints, unknown outcomes, and concurrency

The SHA-256 batch fingerprint covers:

- reconciliation policy meaning;
- complete Intent snapshot and terms;
- complete Ledger and Finality checkpoints;
- every selected Ledger entry field; and
- every selected Finality transition field.

Only the local reconciliation evaluation time is excluded. If SQLite committed
but the caller did not receive the result, a retry at another local time can
still prove the same source facts.

Replay does not trust cursor or fingerprint equality alone. It rereads the
durable report summary, every copied Ledger row, every copied Finality row, and
every discrepancy code. Any changed or extra row raises
`ReconciliationConflictException`.

Two concurrent identical writers race on the unique source key: one returns
`Applied`; the other performs full verification and returns `Replayed`.

## Resource limits and failure order

The policy independently bounds:

- Ledger rows for one payment ID; and
- Finality transitions for each Ledger effect.

The processor requests limit plus one. Seeing the extra row fails before the
Reconciliation transaction, so truncation can never look like a complete report.

| Failure | Behavior |
| --- | --- |
| caller's Intent snapshot changed | fail before Ledger reads |
| caller's Ledger or Finality snapshot changed | fail before evidence commit |
| Finality is not exactly caught up to Ledger | fail during evaluation |
| payment Ledger history exceeds its limit | no report is written |
| one effect's Finality history exceeds its limit | no report is written |
| a source evidence read fails | contextual reconciliation failure, no report |
| malformed source history crosses a reader boundary | evaluation rejects it, no report |
| same source coordinates contain changed facts | checkpoint conflict |
| durable report/evidence was modified | replay conflict |
| caller cancellation | cancellation propagates without a report |

## Five-database reorganization proof

The integration test uses real, independent files for:

1. Payment Intents;
2. Indexer observations;
3. Ledger effects;
4. Finality decisions; and
5. Reconciliation reports.

The first branch contains an exact payment at block 101 and reaches head 103.
Ledger appends its effect, Finality qualifies it at three confirmations, and
Reconciliation appends an `IsConsistent = true` report.

A replacement branch then removes the payment occurrence. Indexer appends its
detach/attach history, Ledger appends the effect reversal, and Finality appends
the qualification revocation. Reconciliation appends a second report containing:

```text
active_payment_missing
reversed_payment_history
amount_underpaid
```

The first consistent report remains queryable. It truthfully describes its old
source snapshot; the second report explains why the current snapshot differs.

## What remains for Week 13 and later

Week 12 still does not add:

- token `Transfer` or balance-delta delivery evidence;
- independent RPCs, trusted anchors, or log-completeness proofs;
- protocol-native safe/finalized consensus evidence;
- accounting journals, merchant balances, refunds, payout authorization, or custody;
- a hosted reconciliation worker, scheduler, retention, backup, or tamper proof;
- transaction signing, broadcasting, nonce lifecycle, SIWE, or API authentication.

Week 13 should begin a test-only transaction lifecycle orchestrator. It must
consume these reports as evidence and must not treat local consistency as an
automatic settlement or signing instruction.

## Suggested reading order

1. `PaymentIntentReadSnapshot.cs` and Payment Intent migration 2.
2. `LedgerReadSnapshot.cs`, `FinalityReadSnapshot.cs`, and their bounded readers.
3. `ReconciliationPolicy.cs` for policy meaning versus resource limits.
4. `ReconciliationEvaluation.cs` for validation, aggregation, codes, and fingerprinting.
5. `PaymentReconciliationProcessor.cs` for source-read and fail-closed order.
6. `ReconciliationDatabaseMigrations.cs` for the durable evidence shape.
7. `SqliteReconciliationStore.cs` for atomic commit and strict replay.
8. Evaluation, persistence, processor, and five-database integration tests.
