# Week 19: exact-block ERC-2612 preflight and durable submission workflow

Week 19 composes the pure Week 18 permit primitive with two new boundaries:

1. a read-only JSON-RPC preflight that observes token identity, EIP-712 domain,
   and `nonces(owner)` at one exact block; and
2. a bounded SQLite workflow that reserves that observed nonce, preserves the
   exact signed calldata, and makes ambiguous submission retry explicit.

The result is still not a broadcaster or payment service. It prepares bytes for
the owner to submit, records caller-reported transport outcomes, and refuses to
equate those outcomes with a receipt, token delivery, finality, accounting, or
settlement.

## Why another preflight is necessary

A valid EIP-712 signature proves what an EOA signed. It does not prove that the
configured token currently has the expected runtime code, returns the expected
domain, or still has the nonce embedded in the signature. Week 19 therefore
adds `Erc2612TokenTrustPolicy`, which combines the Week 18 permit policy with a
reviewed runtime-code Keccak.

For one owner, `JsonRpcErc2612TokenSnapshotRpc` reads:

```text
eth_chainId
eth_getBlockByNumber("latest", false) -> block number + hash
eth_getCode(token, exact block number)
eth_call token.name() at the exact block number
eth_call token.DOMAIN_SEPARATOR() at the exact block number
eth_call token.nonces(owner) at the exact block number
eth_getBlockByNumber(exact block number, false) -> number + hash again
```

Every state-bearing token read uses the captured block number, not `latest`.
The second header read rejects a block-hash change during the observation. This
closes mixed-height reads and detects a reorg during the snapshot; it does not
make one RPC endpoint honest or prove that the block is finalized.

The adapter also bounds a response to 256 KiB, requires matching JSON-RPC IDs,
rejects error/missing results, decodes canonical quantities, and strictly
decodes the dynamic ABI string returned by `name()`. RPC exceptions are
sanitized at the preflight boundary because an endpoint, response, or contract
return may contain credentials or attacker-controlled text.

## What the verified token snapshot means

`Erc2612PermitPreflightService` accepts an observation only when all of these
facts agree:

- observed chain equals the reviewed Anvil/Sepolia policy;
- requested and returned token/owner identities are exact;
- runtime bytecode Keccak equals the reviewed code hash;
- `name()` equals the reviewed printable token name;
- `DOMAIN_SEPARATOR()` equals a locally recomputed EIP-712 domain separator;
- block number, block hash, and owner nonce are canonical and bounded.

The resulting `VerifiedErc2612TokenSnapshot` records owner, nonce, block
coordinates, runtime-code hash, and domain separator. It is evidence of a
policy-matched view from one provider at one block, not a trusted-block proof.
The EIP-712 `version` is covered indirectly by the recomputed configured domain
separator because the ERC-2612 interface does not standardize a `version()`
method.

## Reserve before returning typed data

`Erc2612PermitWorkflow.ReserveAsync` first performs the preflight, constructs
the deterministic Week 18 draft with the observed nonce, and then starts an
immediate SQLite transaction. The store owns this uniqueness constraint:

```text
UNIQUE(chain_id, token_address, owner_address, token_nonce)
```

Only after that reservation commits does the caller receive the typed data.
Two processes sharing the same file cannot independently create different
drafts for the same token nonce. An exact concurrent replay receives the one
existing operation; a different amount or draft at the same nonce is a stable
conflict.

The database has a pinned capacity from 1 through 100,000 operations. It keeps
the complete bounded history and fails closed when full instead of deleting an
ambiguous permit record to regain space.

## Immutable preparation and append-only states

After an external wallet signs the returned JSON,
`VerifyAndPrepareAsync` runs the Week 18 canonical EOA recovery and Router
identity checks, then stores the exact signature-bearing `payWithPermit`
calldata once. The operation and preparation rows are immutable; state changes
are append-only transitions:

```text
Reserved -> Prepared -> SubmissionUnknown -> SubmissionAccepted
                         |                  -> SubmissionRejected
                         +-> SubmissionUnknown (explicit identical retry)

Reserved / Prepared / SubmissionUnknown / SubmissionAccepted
  -> NonceChanged or Expired
```

`SubmissionAccepted` means only that the caller reported an accepting transport
response. A later nonce observation may still append `NonceChanged`; the code
never renames it to `Consumed`, because the observed nonce alone cannot prove
which transaction advanced it.

SQLite `STRICT` tables, foreign keys, checks, and triggers reject row mutation,
deletion, or illegal state edges. Reads additionally rebuild the EIP-712 draft
and compare its policy fingerprint, typed data, domain separator, struct hash,
and digest. Prepared calldata is checked against its Keccak and duplicate ABI
facts (`PaymentId`, token, merchant, amount, deadline, and signature word
shape). These checks detect accidental/internal inconsistency. They are not
cryptographic tamper evidence against an operator who can rewrite the complete
database and schema.

## Persist unknown before bytes escape

The dangerous crash window is between sending a transaction and recording what
happened. `BeginSubmissionAsync` therefore performs the steps in this order:

```text
load Prepared at transition T
  -> reject exact deadline expiry without another RPC call
  -> reobserve token identity/domain/current nonce
  -> compare nonce with the reserved draft
  -> atomically append SubmissionUnknown based on transition T
  -> only then return exact persisted calldata + RequiredSender
```

If the process crashes after returning calldata, restart sees
`SubmissionUnknown`; it cannot silently create a fresh permit or pretend that
nothing escaped. If the nonce changed before release, the workflow appends
`NonceChanged` and returns no calldata.

An explicit retry returns the exact stored bytes. The caller must also pass the
transition ID it observed. Concurrent callers using the same observed version
compete in one compare-and-append transaction, so only one obtains a new retry
authorization. A transport outcome must name the exact authorization transition;
a stale outcome cannot overwrite a newer retry.

The workflow coordinates only the token permit nonce. The owner account's
transaction nonce belongs to the separate transaction lifecycle boundary and
must be managed independently by whichever component actually submits the
owner transaction.

## Sensitive data and redaction

The SQLite preparation necessarily contains the complete permit signature in
calldata so an identical restart retry is possible. The file is local,
unencrypted sensitive data. It requires operating-system access control and must
not be copied into logs, diagnostics, telemetry, source control, or support
bundles.

`PermitPaymentPreparation`, `PermitWorkflowSnapshot`, and
`PermitSubmissionAuthorization` redact typed data/signature calldata from their
string representations. Adapter and corruption errors use fixed messages rather
than echoing RPC responses or stored bytes.

## Executable evidence

Focused tests cover:

- exact chain/code/name/domain/owner/nonce matching and sanitized failures;
- raw JSON-RPC exact-block tags, strict ABI decoding, and reorg rejection;
- restart-safe nonce reservation and exact replay/conflict behavior;
- concurrent reservation, first submission, and version-bound retry races;
- durable unknown-before-release ordering and exact-byte retry;
- stale authorization outcome rejection;
- expiry without unnecessary RPC, nonce-change handling, and the intentionally
  weak meaning of transport acceptance;
- idempotent migrations, database-owned capacity, immutable rows, illegal
  transition rejection, and read-time calldata corruption detection.

The 2026-08-30 clean committed snapshot at implementation commit
[`e308e1b`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/e308e1b)
passed 333/333 .NET tests, including 31/31 focused Permit tests, 58/58
Authentication tests, and 45/45 API tests. It also passed all 36 unchanged
Foundry tests, the reviewed 1,030-byte/zero-slot Router baseline, and isolated
deployment plus signed Anvil lifecycle replay from 1,294 tracked files. The
dynamic canary and working-tree/complete 47-commit history scans found no leaks.
GitHub Actions run
[`33312644170`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33312644170)
independently passed locked .NET, Foundry plus signed RPC replay, and secret-scan
jobs for the pushed evidence commit.

## Residual limitations

Week 19 deliberately has no:

- trusted block, independent provider comparison, log/receipt observation, or
  protocol finality proof;
- actual wallet UI, private-key signer, transaction nonce manager, broadcaster,
  receipt poller, relayer, HTTP endpoint, or hosted worker;
- proof that `SubmissionAccepted` was mined or that `NonceChanged` was caused by
  this operation;
- cross-host coordination, database encryption/backup/tamper evidence, or
  cleanup/archival protocol for a full store;
- ERC-1271 contract-wallet support or alternate dialects such as DAI Permit and
  Permit2;
- production token/deployment registry, Sepolia end-to-end evidence,
  fee-on-transfer/rebasing support, or audited deployment; or
- merchant balance, accounting credit, authorization, payout, or settlement
  evidence.

The nonce and signature remain front-runnable. An observer can submit the same
permit directly to the token first, advance the nonce, and deny the combined
Router call. The fixed spender/value prevents redirecting that allowance to an
attacker-selected spender, but it does not eliminate denial of service.

## Suggested reading order

1. `Erc2612TokenTrustPolicy.cs` for the reviewed runtime/domain envelope.
2. `JsonRpcErc2612TokenSnapshotRpc.cs` for exact-block JSON-RPC and ABI parsing.
3. `Erc2612PermitPreflightService.cs` for policy matching and sanitization.
4. `Erc2612PermitWorkflow.cs` for the public orchestration order.
5. `PermitWorkflowDatabaseMigrations.cs` for immutable rows and legal states.
6. `SqlitePermitWorkflowStore.cs` for atomic reservation and compare-and-append.
7. the Preflight, Workflow, and Persistence tests for failure/restart races.
