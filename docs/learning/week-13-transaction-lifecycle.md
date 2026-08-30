# Week 13: test-only transaction lifecycle

Week 13 adds `PaymentSandbox.Orchestrator`, an independently testable class
library for learning the difficult part of transaction submission: durable
nonce reservation, exact retry after an unknown broadcast, fee-only
replacement, and receipt observation.

It is deliberately not a wallet application. There is no private key, mnemonic,
production signer, hosted worker, HTTP endpoint, automatic scheduler, or real
RPC adapter in this milestone. Tests use deterministic fakes and temporary
SQLite files.

## The problem being modelled

Sending a transaction is not one atomic call from the application's point of
view. The node may accept bytes and the network response may disappear. If the
application treats that timeout as “nothing happened” and creates another
payment, it can transfer value twice.

The safe question after an ambiguous response is therefore not “how do I send
again?” It is “which exact signed bytes did I already attempt to send?”

Week 13 persists the answer before broadcasting.

## Boundary and non-goals

The Orchestrator accepts one explicit `PaymentTransactionRequest`. It uses an
already `VerifiedPaymentRouterClient` to encode the reviewed `pay` selector and
requires a `TransactionLifecyclePolicy` for the same chain and Router.

The policy permits only:

- local Anvil, chain ID `31337`; or
- Ethereum Sepolia, chain ID `11155111`.

Every other chain, including Ethereum mainnet, fails before nonce observation or
signing. The policy also fixes the signer, Router, gas ceiling, fee ceilings,
minimum replacement bump, attempt limit, and maximum local nonce lead.

This module does not read Reconciliation and never turns `IsConsistent` into a
signing command. Pre-payment transaction creation and post-observation
reconciliation are separate use cases; joining them automatically would create
an unsafe circular authority.

## Four append-only histories

The database does not store a mutable `status` column. Current state is derived
from four kinds of durable fact:

1. `transaction_operations` stores the immutable payment request, policy,
   encoded calldata, observed pending nonce, and locally reserved nonce.
2. `transaction_attempts` stores the initial signed bytes and any fee-only
   replacements. Every attempt uses the same signer nonce, Router, calldata,
   gas limit, and zero native value.
3. `transaction_broadcast_observations` records `accepted`, `already_known`,
   `unknown`, or `rejected` observations without overwriting earlier evidence.
4. `transaction_receipt_observations` records at most one mined attempt for the
   operation.

The derived states are:

| State | Durable meaning |
| --- | --- |
| `Reserved` | operation and nonce exist; no signed attempt exists |
| `Signed` | current attempt is durable; it has no broadcast observation |
| `BroadcastUnknown` | current attempt may have been accepted, but no positive acceptance is known |
| `Submitted` | at least one endpoint accepted or already knew the current exact bytes |
| `Rejected` | current attempt has no positive acceptance and its effective observation is rejected |
| `MinedSucceeded` | one stored receipt reports EVM status success |
| `MinedReverted` | one stored receipt reports EVM status revert |

Positive acceptance dominates a later timeout or rejection for the same bytes.
This matters when two concurrent calls broadcast the same payload and their RPC
responses arrive in the opposite order. The later ambiguous response must not
erase earlier positive evidence.

`MinedSucceeded` is still only a receipt observation. It is not protocol
finality, token balance-delta proof, accounting credit, or settlement. Week 11
and Week 12 retain their separate meanings.

## Create order

`CreateAsync` uses this fail-closed order:

1. validate request values and hard policy limits;
2. encode exact Router `pay` calldata through the verified client;
3. look for the operation ID in durable storage;
4. only for a new operation, read the account's pending nonce;
5. reserve `max(RPC pending nonce, last local nonce + 1)` in an immediate SQLite
   transaction;
6. build the complete intended EIP-1559 transaction;
7. call the test signer abstraction;
8. persist signed bytes, hash, byte length, fees, and unsigned-fact fingerprint;
9. return without broadcasting.

Reservation happens before signing so a signer failure leaves a recoverable
`Reserved` operation. A retry reuses its durable nonce and does not require the
nonce RPC to recover.

Different operations for the same chain and signer are serialized by an
immediate SQLite transaction and the unique `(chain_id, signer_address, nonce)`
constraint. This coordinates processes only when they use the same database
file. It is not cross-host nonce coordination.

## Unknown broadcast and exact retry

`BroadcastAsync` first loads the current persisted payload. It never accepts a
fresh payload from the caller.

If the broadcaster throws after a possible side effect, the processor records
`unknown/transport_error`. A later call reloads and broadcasts the same raw hex,
with the same transaction hash. It does not reserve a nonce, invoke the signer,
or create another attempt.

The raw signed transaction is intentionally stored because exact retry is
impossible without it. It is sensitive replay material:

- `SignedTransactionPayload.ToString()` and debugger display redact it;
- public lifecycle snapshots expose hashes and counts, not raw bytes;
- boundary exceptions discard untrusted adapter messages and inner exceptions;
- the database is local, unencrypted test data and must not be published,
  attached to issues, or treated as a key vault.

## Fee-only replacement

A replacement is allowed only after an earlier broadcast observation. It keeps:

- chain ID and signer;
- Router destination;
- account nonce;
- gas limit;
- zero native value; and
- the exact `pay(paymentId, token, merchant, amount)` calldata.

Only `maxFeePerGas` and `maxPriorityFeePerGas` may change. Both fields must meet
the configured percentage bump, rounded upward, and remain below policy caps.
Attempt count is bounded in the processor, store, and database trigger.

All possibly submitted same-nonce attempts are eligible for receipt lookup.
Ethereum permits at most one of them to be mined. If an RPC claims receipts for
multiple replacements with one signer nonce, the processor records none and
fails closed.

## Durable verification

The SQLite migration creates four `STRICT` tables, foreign keys, uniqueness
constraints, shape checks, and triggers. Triggers prevent:

- skipped attempt sequence numbers or a changed nonce;
- attempts beyond the durable policy limit;
- a new attempt or broadcast after a receipt;
- a receipt for another operation/attempt/hash; and
- a receipt without a possibly accepted broadcast observation.

Application reads add semantic verification that SQL shape checks cannot
express cleanly. Before signed bytes leave the store, it:

- recomputes Keccak-256 and byte length from raw bytes;
- verifies attempt sequence and the operation nonce;
- rechecks initial fees, replacement bumps, and durable caps; and
- reconstructs the complete unsigned transaction and recomputes its SHA-256
  fingerprint.

An operation-ID replay also compares every immutable request and policy fact.
The observed pending nonce and timestamps do not become caller-controlled
replacement facts.

## Important residual limitation

The signer interface returns opaque signed bytes. Week 13 proves lifecycle
handling around that interface, but it does not provide a real Nethereum signer
adapter and does not decode/recover the returned EIP-1559 transaction to prove
that the opaque bytes actually contain the intended chain, signer, nonce,
destination, fees, zero value, and calldata.

For that reason, the unsigned fingerprint proves what the Orchestrator asked the
test signer to sign; it is not cryptographic attestation of what an arbitrary
signer returned. A future adapter must round-trip and verify those signed fields
before any real Anvil/Sepolia broadcast is acceptable.

## Failure matrix

| Failure | Durable result | Safe retry |
| --- | --- | --- |
| pending nonce RPC fails before reservation | no operation | retry after RPC recovery |
| signer fails after reservation | `Reserved` | same operation and nonce; sign again |
| process stops after attempt commit | `Signed` | broadcast stored bytes |
| broadcast response is lost | `BroadcastUnknown` | broadcast the same stored bytes |
| node rejects current bytes | `Rejected` | explicit policy-approved replacement |
| receipt is absent | unchanged | query again; do not infer failure |
| two same-nonce receipts are reported | unchanged | investigate RPC inconsistency |
| durable raw/fingerprint/fee facts are changed | conflict | stop and investigate database integrity |

## Verification coverage

The focused tests cover policy allowlisting and caps, rounded fee bumps, durable
reservation recovery, exact unknown rebroadcast, accepted-evidence dominance,
replacement invariants, attempt limits, success/revert receipts, contradictory
same-nonce receipts, non-leaking exceptions, migration idempotency, direct SQL
trigger enforcement, concurrent nonce allocation, operation replay conflicts,
nonce lead limits, and raw/fingerprint tamper detection.

The 2026-08-30 committed-snapshot verification passed 223/223 .NET tests,
including 30/30 focused Orchestrator tests. The same run passed all 36 unchanged
Foundry tests, the reviewed 1,030-byte/zero-storage-slot Router baseline, and
successful/reverted Anvil replay from 1,209 Git-known files. The dynamic secret
canary, candidate-tree scan, and complete 29-commit history scan found no leaks.
Implementation commit `60f88c1` records the verified code boundary. Remote CI
evidence is added only after the pushed commit finishes.

## Suggested reading order

1. `TransactionLifecyclePolicy.cs` for network and resource authority.
2. `PaymentTransactionRequest.cs` and `UnsignedPaymentTransaction.cs` for facts.
3. `TransactionLifecycleProcessor.cs` for side-effect order and recovery.
4. `TransactionLifecycleSnapshot.cs` for the derived public state.
5. `TransactionLifecycleDatabaseMigrations.cs` for durable invariants.
6. `SqliteTransactionLifecycleStore.cs` for nonce arbitration and replay checks.
7. Processor, policy, and persistence tests for executable failure examples.

## What should come next

Week 14 should add a bounded local-Anvil signer/broadcaster adapter with an
ephemeral test wallet, decode-and-recover verification of every signed field,
and a real replacement/unknown-result integration test. It must keep key
material out of source, logs, CI, and durable lifecycle rows, and it must not
weaken the explicit chain allowlist.
