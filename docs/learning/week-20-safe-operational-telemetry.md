# Week 20: low-cardinality, payload-blind operational telemetry

Week 20 adds provider-neutral tracing and metrics around the two boundaries that
can release replay-sensitive material: the ERC-2612 permit workflow and the
test transaction lifecycle. The implementation uses only .NET
`System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics`; it does
not choose an exporter, collector, monitoring vendor, dashboard, or alerting
service.

This distinction matters. Instrumentation defines what the application is
allowed to reveal. Export and retention are deployment decisions that cannot
repair sensitive or unbounded data already attached by application code.

## 1. The threat is observability itself

The observed workflows handle values that must not become ordinary diagnostic
data:

- ERC-2612 typed data, signature, and signature-bearing Router calldata;
- signed raw EIP-1559 transactions;
- RPC URLs or provider responses that may contain credentials;
- exception messages, inner exceptions, stacks, or custom `Data` values;
- wallet, token, merchant, and Router addresses;
- payment, permit operation, transaction operation, attempt, block, and
  transaction identifiers.

Some identifiers are public on-chain facts, but they are still unsuitable as
metric dimensions. Their cardinality grows with traffic and can make a metrics
backend expensive or unusable. They can also turn an operational dataset into
an unnecessary history of wallet activity.

Week 20 therefore makes the safe path the only path exposed by
`IOperationalTelemetry`: `BeginOperation` receives one component enum and one
action enum, and returns a scope that accepts only `Complete(outcome)` or
`Fail(failureCode)`. There is no arbitrary tag dictionary and no overload that
accepts a payload, identifier, generic result, exception, or message.

`OperationalExecution` owns the async call and classification outside the
injected observer. It reduces the result or exception to an enum before calling
the scope. An injected implementation therefore cannot retain the operation
delegate, business result, or exception object.

## 2. Dependency direction

`PaymentSandbox.Observability` is a dependency-free class library:

```text
PaymentSandbox.Observability
  ^                         ^
  |                         |
PaymentSandbox.Permits   PaymentSandbox.Orchestrator
```

It does not reference Domain, RPC, SQLite, contracts, permits, or the
orchestrator. The two workflows know how to classify their own public results
and sanitized exception types. This keeps business meaning in the consumer and
keeps the instrumentation library ignorant of sensitive domain objects.

Both workflows default to `OperationalTelemetry.Shared`. Tests or a future
host can inject another `IOperationalTelemetry`, but the workflow-facing
contract remains payload-blind.

## 3. Stable activities and instruments

The instrumentation name and version are:

```text
PaymentSandbox.OperationalTelemetry / 1.0.0
```

Each call creates an internal Activity named from a fixed action, for example:

```text
payment_sandbox.permit.begin_submission
payment_sandbox.transaction.broadcast
```

The library emits three metric instruments:

| Instrument | Type | Unit | Meaning |
| --- | --- | --- | --- |
| `payment_sandbox.operation.completed` | counter | `{operation}` | Calls completed, including failure and cancellation |
| `payment_sandbox.operation.duration` | histogram | `ms` | End-to-end boundary duration |
| `payment_sandbox.operation.active` | up/down counter | `{operation}` | Calls currently inside the boundary |

`active` receives `+1` before invoking the operation and `-1` in `finally`, so
success, failure, and caller cancellation all balance. `completed` and
`duration` are also emitted from `finally`, ensuring failed calls remain
observable.

## 4. The complete label allowlist

Activities and completed/duration measurements use only:

```text
payment_sandbox.component
payment_sandbox.action
payment_sandbox.outcome
payment_sandbox.failure_code
```

The active counter uses only component and action because its measurement is
made before the result exists. Every value comes from an exhaustive enum-to-
string switch. An unknown enum value fails instead of silently creating a new
time series.

Outcomes distinguish `created`, `applied`, `replayed`, and `no_work`. These are
successful control-flow facts, not HTTP or chain success. Failures use coarse
categories such as `invalid_input`, `policy_rejected`, `workflow_rejected`,
`state_conflict`, `persistence_failure`, and `dependency_failure`.

The transaction lifecycle has an older sanitized exception without a
machine-readable subtype. Its classifier deliberately reports the coarse
`workflow_rejected` category and never parses exception text. Guessing a label
from text would couple telemetry to wording and tempt future code to inspect
untrusted messages.

## 5. Trace failure behavior

Successful and no-work Activities receive `ActivityStatusCode.Ok`. Failed and
caller-cancelled Activities receive `ActivityStatusCode.Error`, but no status
description and no exception event. The original exception is rethrown with
its stack; it remains available to the caller without being copied into the
telemetry export surface.

Caller cancellation is a separate bounded result (`cancelled/cancelled`). An
`OperationCanceledException` from a dependency when the caller token was not
cancelled follows the normal failure classifier instead of falsely reporting a
cooperative cancellation.

## 6. Workflow coverage

The permit workflow observes:

- reserve;
- verify and prepare;
- begin submission;
- retry unknown submission;
- record transport outcome; and
- refresh usability.

The transaction lifecycle observes:

- create/reserve/sign;
- broadcast;
- fee-only replacement; and
- receipt refresh.

The boundary wraps the complete public method, so validation, RPC, signing,
persistence, replay/no-work decisions, and result classification share one
duration. It does not create nested spans for raw RPC calls because that would
require a separate review of endpoint, request, response, and exception
redaction.

## 7. Executable evidence

`PaymentSandbox.Observability.Tests` attaches real `ActivityListener` and
`MeterListener` instances and proves:

1. a successful call emits one exact trace shape, one completion, one duration,
   and balanced active measurements;
2. an exception containing a credential-like RPC URL and signed bytes is
   rethrown but absent from every captured Activity tag and metric tag;
3. caller cancellation gets its stable category and still balances active
   measurements.

Focused Permit and Orchestrator tests inject payload-blind recording observers.
They prove real workflow dispositions map to the expected component/action/
outcome tuples. The fake cannot retain the generic result or exception object
because neither crosses its interface, mirroring the production contract's
narrow data surface.

## 8. What this week does not add

There is no exporter, collector, backend, dashboard, alert, sampling policy,
trace propagation, secure audit log, retention policy, incident runbook,
service-level objective, or production hosting configuration. No Activity is a
payment receipt, finality proof, settlement fact, or authorization decision.

A later host must review exporter defaults and resource attributes before
enabling export. In particular, it must not automatically copy exception
details, HTTP URLs, process arguments, environment variables, database paths,
or request bodies into telemetry.

Week 21 should use this stable signal surface while adding deterministic fault
injection and recovery tests. It should not broaden the tag allowlist merely to
make a fault easier to debug.

## 9. Local committed-snapshot evidence

On 2026-08-30, the clean committed implementation snapshot `8b136b4` passed
339/339 .NET tests, including 4/4 focused Observability, 43/43 Orchestrator, and
32/32 Permit tests. The unchanged contract suite passed 36/36 Foundry tests and
the reviewed Router remained 1,030 runtime bytes with zero storage slots.

The isolated deployment and signed Anvil lifecycle replay passed from 1,306
tracked files. The dynamic secret canary, current working tree, and complete
50-commit history scans found no leaks. GitHub Actions run
[`33317338502`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33317338502)
then independently passed the locked .NET, Foundry plus signed RPC replay, and
secret-scan jobs.
