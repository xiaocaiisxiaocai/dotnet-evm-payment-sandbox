# Week 21: deterministic post-commit fault recovery

Week 21 tests the narrow but dangerous interval in which a workflow has
committed its durable state and then loses the public method response. The
caller sees an exception and cannot know from that exception alone whether the
operation committed. A safe retry must inspect durable state and must not
repeat an irreversible or sensitive side effect.

This week adds no chaos framework, production crash switch, broadcaster, or
background retry worker. It adds a test-only deterministic fault boundary,
exercises both payload-release workflows through that boundary, and closes two
idempotency gaps found by the tests.

## 1. The failure window

Ordinary dependency-failure tests answer questions such as “what happens when
signing fails?” or “what happens when RPC times out?”. They do not prove the
different sequence below:

```text
caller       workflow           SQLite / side effect
  |              |                       |
  | call         |                       |
  |------------->| mutate / observe      |
  |              |---------------------->|
  |              | commit succeeds       |
  |              |<----------------------|
  |              X response is lost      |
  |<-- exception-|                       |
  | retry        | read durable state     |
  |------------->|---------------------->|
  |              | return replay/no-work |
  |<-------------|                       |
```

The first exception is deliberately ambiguous. The durable database, not the
caller stack or exception text, decides what the retry may do.

## 2. Why the fault is injected through telemetry

Week 20 placed `OperationalExecution` around each public Permit and transaction
lifecycle operation. Its order is:

1. execute and await the business operation;
2. classify the returned result;
3. notify the operation observer;
4. return the result to the caller.

`OneShotPostCompletionFaultTelemetry` is a test-only observer that throws at
step 3 for one exact action/outcome pair. Reaching that point proves the async
business operation already returned successfully. The caller receives an
`InjectedPostCompletionFaultException`, while any SQLite commit and external
call performed by the operation remain observable.

The fault is deterministic because a test explicitly arms both dimensions:

```csharp
fault.Arm(
    OperationalAction.TransactionBroadcast,
    OperationalOutcome.Applied);
```

Requiring the expected outcome prevents the fault from accidentally moving to
a later replay or no-work call. After one match it disarms itself and increments
`TriggerCount`.

This is not a production crash hook. The production workflows still receive
the same payload-blind `IOperationalTelemetry` interface. The utility lives in
`tests/PaymentSandbox.Testing`, and production projects do not reference it.

## 3. Why a timing race or cancellation is weaker evidence

A delay plus cancellation cannot identify whether cancellation happened before
the write, during a provider call, during SQLite commit, or after the result was
computed. Such a test may pass for the wrong timing and become flaky on another
machine.

Mocking a store to throw “after commit” is also easy to misapply: every store
method needs a wrapper, and the wrapper can accidentally throw before an async
commit has really completed. The already reviewed operation boundary gives one
precise point shared by all public methods without changing their persistence
interfaces.

## 4. Transaction lifecycle recovery matrix

| Lost response after | Durable evidence | Exact retry | Side effect that must not repeat |
| --- | --- | --- | --- |
| create | `Signed`, attempt 1 | `NoWork` | nonce RPC read and signing |
| accepted broadcast | `Submitted` | `NoWork` | raw transaction broadcast |
| replacement | `Signed`, attempt 2 with requested fee | `NoWork` | signing attempt 3 |
| receipt refresh | `MinedSucceeded` | `NoWork` | receipt RPC read |

The tests assert both state and call counts. For example, create recovery proves
one nonce read and one signed transaction across the failed response and retry.
Broadcast recovery proves the broadcaster receives the raw transaction once,
not merely that the second method returns without throwing.

### Replacement idempotency fix

Before Week 21, a successful replacement leaves the operation in `Signed` so
that the new attempt can be broadcast. A repeated `ReplaceAsync` therefore
looked like an invalid request and failed, even when the caller supplied the
exact fee whose earlier commit response was lost.

The processor now loads the current signed payload. If it is a later attempt
and its complete `TransactionFeeQuote` equals the requested quote, the method
returns `NoWork`. It does not sign another same-nonce transaction. Attempt 1 is
excluded, so an initial signed transaction cannot be mislabeled as a completed
replacement. A different fee still fails until the current replacement has a
broadcast observation.

## 5. Permit workflow recovery matrix

| Lost response after | Durable evidence | Exact retry | Recovery meaning |
| --- | --- | --- | --- |
| reserve | one reserved operation for observed nonce/facts | `Replayed` | returns the same operation |
| prepare | immutable matching preparation | `Replayed` | returns the same calldata facts |
| begin submission | `SubmissionUnknown` committed | explicit `RetryUnknownAsync` | releases exact persisted bytes under a new authorization edge |
| record accepted/rejected | exact authorization/outcome edge | `Replayed` | does not append a second outcome |
| expiry refresh | `Expired` | `NoWork` | does not read token RPC again |

Begin-submission recovery is intentionally different from an automatic replay.
The first authorization may have escaped even though its response did not reach
this caller. Durable `SubmissionUnknown` preserves that ambiguity. An operator
must read its transition ID and explicitly request the existing workflow's
unknown retry; the workflow persists another authorization before returning the
same calldata.

### Outcome idempotency fix

Before Week 21, `RecordSubmissionOutcomeAsync` required the authorization to be
the latest transition and the state to be `SubmissionUnknown`. After a
successful outcome commit, an exact retry necessarily failed that check.

The store now first verifies the historical edge:

```text
supplied transition ID: submission_unknown
its next transition for the same operation: submission_accepted OR submission_rejected
```

If the next transition is the exact requested outcome, the call returns
`Replayed` with the current snapshot and writes nothing. Transition IDs are
global SQLite `AUTOINCREMENT` values, so the two rows need not have adjacent
numbers; ordering is evaluated within the operation. The opposite outcome is
still rejected, preventing a retry from rewriting accepted evidence as
rejected or vice versa.

## 6. What the tests prove

The focused tests establish that:

1. the injected exception occurs only after the selected successful result;
2. durable state remains visible after the caller receives that exception;
3. an exact retry reaches replay/no-work behavior;
4. nonce reads, signing, broadcasting, receipt reads, and terminal RPC reads
   are not repeated where the durable state already answers the question;
5. Permit authorization retries release byte-for-byte persisted calldata;
6. exact Permit outcome retries append no duplicate transition; and
7. a contradictory Permit outcome still fails closed.

The fault observer itself wraps a payload-blind recording observer. It sees only
the fixed Week 20 action and outcome enums; it does not gain access to calldata,
raw signed transactions, identifiers, results, or exception objects.

## 7. What the tests do not prove

The process is not forcibly terminated, SQLite files are not corrupted, and no
OS power-loss semantics are simulated. The tests do not prove filesystem
durability under hardware failure, cross-host coordination, provider honesty,
automatic retry scheduling, retry backoff, incident alerting, or restoration
from backup.

The test fault occurs after a complete public operation. For transaction create
that means after the signed-attempt commit, not between nonce reservation and
signing. The earlier signer-failure test separately covers that window and
proves the durable reservation can resume without another nonce read.

The absence of repeated calls in these fakes does not make all production
dependencies idempotent. A future hosted worker still needs explicit retry
ownership, attempt budgets, timeouts, operator-visible ambiguity, and runbooks.

## 8. Test cleanup isolation

Formal Release verification exposed a separate test-infrastructure race. Each
temporary SQLite helper calls `SqliteConnection.ClearAllPools()` before deleting
its Windows directory. That method is process-wide: if xUnit runs two test cases
in the same assembly concurrently, one case can clear a pooled handle while the
other is opening it, producing a non-deterministic `ObjectDisposedException`.

Every affected SQLite test assembly now uses xUnit's assembly-level
`Parallelization(Mode = ParallelMode.None)`. This serializes independent test
cases inside that process. It does not weaken the explicit concurrency tests:
their `Task.WhenAll` calls still create simultaneous workflow/store operations
inside one test. Different test projects also remain separate processes and can
run concurrently under `dotnet test`.

## 9. Recommended reading order

1. `tests/PaymentSandbox.Testing/Faults/OneShotPostCompletionFaultTelemetry.cs`;
2. `src/PaymentSandbox.Observability/OperationalExecution.cs`;
3. the post-commit tests in `TransactionLifecycleProcessorTests.cs`;
4. `TransactionLifecycleProcessor.ReplaceCoreAsync`;
5. the post-commit tests in `Erc2612PermitWorkflowTests.cs`;
6. `SqlitePermitWorkflowStore.RecordOutcomeAsync` and
   `IsOutcomeReplayAsync`.

## 10. Next boundary

Week 22 should turn the now-explicit ambiguous states and retry rules into an
operator runbook. It should define observation, safe retry, escalation, and
stop conditions without claiming that metrics or exception text are settlement
evidence.

## 11. Local committed-snapshot evidence

On 2026-08-30, the clean committed implementation snapshot `8616f4d` plus
SQLite test-isolation commit `484f9e1` passed 349/349 .NET tests, including 4/4
focused Observability, 47/47 Orchestrator, and 38/38 Permit tests. The unchanged
contract suite passed 36/36 Foundry tests and the reviewed Router remained 1,030
runtime bytes with zero storage slots.

The isolated deployment and signed Anvil lifecycle replay passed from 1,318
tracked files. The dynamic secret canary, current working tree, and complete
54-commit history scans found no leaks.
