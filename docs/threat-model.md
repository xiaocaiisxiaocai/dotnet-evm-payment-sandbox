# Threat Model v1

- Status: Week 17 loopback SIWE session update to the Week 1 security baseline
- Last updated: 2026-08-30
- Owner: repository maintainer

## 1. Purpose

This document identifies what the project currently protects, where trust boundaries exist, and which risks remain unresolved. It is not a security audit and does not show that the project is ready for production. Any change to the signing path, funds flow, network, or deployment boundary requires a matching threat-model review.

## 2. Scope

The current model covers:

- The developer workstation, Git working tree, and repository-local `.tools` directory.
- The GitHub repository and read-only GitHub Actions workflows.
- The .NET solution and the test-only PaymentRouter/TestUSDC implementation, including the typed contract adapter, runnable SQLite-backed Payment Intent API, bounded SIWE challenge verification, chain-observation/checkpoint library, provisional reversible ledger, reversible confirmation qualification, explainable reconciliation reports, test-only transaction lifecycle, identity checks, permit, fuzz, invariant, and local deployment tests.
- Local Anvil, plus one later smoke test on Ethereum Sepolia.
- Protocol-native finality, token-delivery proof, accounting, production/public-network signer and RPC adapters, production browser hosting, and authorization planned for later gates.

The following are explicitly out of scope:

- Ethereum mainnet or any network that carries real value.
- Real customers, orders, production funds, custody, payouts, or yield claims.
- Production KMS, 24x7 operations, disaster recovery, multichain support, or public API hosting.
- A third-party audit, formal verification, or legal opinion.

Passing Gate A therefore means that the learning repository has a reproducible baseline. It does not mean that the system can be used in production.

## 3. Data Flow and Trust Boundaries

```text
Developer
  | local commands and Git changes
  v
Workstation -------- downloads --------> GitHub/.NET/Foundry/Gitleaks releases
  |                                      (external supply-chain boundary)
  | push or pull request
  v
GitHub Actions (read-only, no signing secret)

Later data flow:
HTTP client -> local API -> local SQLite intent store
Payer -> Anvil/Sepolia -> untrusted RPC -> bounded Indexer batch -> local SQLite observations/checkpoint
Indexer transition log -> provisional Ledger effects/reversals -> separate local SQLite checkpoint
Indexer head snapshot + caught-up Ledger -> reversible confirmation qualification
Intent + caught-up Ledger/Finality snapshots -> append-only explainable reconciliation
Test signing request -> Orchestrator -> ephemeral process-local wallet -> loopback Anvil
Configured relying party -> loopback SIWE challenge -> browser binding -> EOA signature -> opaque SQLite session
```

Boundary assumptions:

- Download sites and third-party Actions are supply-chain boundaries. A version label alone is insufficient; Actions use commit SHAs and downloaded archives use fixed SHA-256 values.
- RPC output is untrusted. Week 5 checks self-reported chain ID and latest code at an operator-configured address against a reviewed runtime hash. Week 8 additionally requires an explicit range, matching chain ID, complete numbered blocks, parent continuity, bounded logs, exact emitter/block occurrence fields, and atomic observation/checkpoint persistence. Week 9 bounds common-ancestor search, retains both forks, and atomically records detach/attach transitions. One endpoint can still lie or omit data; trusted blocks, independent cross-checks, completeness evidence, confirmations, and finality remain later controls.
- Ledger input is derived from the local Indexer, not from an independent truth source. Week 10 consumes only committed transition IDs, bounds transitions/payments, preserves exact occurrence identity, appends linked reversals, and atomically advances its own source checkpoint. This contains duplicate/lost local effects but does not make a dishonest or incomplete RPC observation true.
- Finality input is still derived from the same local RPC observation path. Week 11 atomically pairs the Indexer head with its transition watermark, requires Ledger to be exactly caught up, requires the exact Ledger entry high-watermark, and appends reversible threshold decisions. This prevents known-but-unconsumed local reversals from being qualified; it does not prove provider honesty, log completeness, consensus finalization, or economic irreversibility.
- Reconciliation compares only locally derived facts. Week 12 atomically watermarks Intent, Ledger, and Finality reads; requires exact cross-source catch-up; bounds per-payment histories; and appends complete evidence plus stable discrepancy codes. This makes partial, duplicate, mismatched, and reversed histories explainable, but a consistent report is not token delivery, protocol finality, accounting credit, or permission to settle.
- Orchestrator adapters are untrusted side-effect boundaries. Week 13 allowlists only Anvil/Sepolia, binds one verified Router and signer policy, persists before broadcast, repeats exact signed bytes after an unknown result, and recomputes durable raw/unsigned identities before reuse. Week 14 adds a concrete generated-key path that is narrower than the general policy: credential-free loopback HTTP, an Anvil client, chain `31337`, reviewed Router runtime, canonical type-2 re-encoding, exact unsigned-field comparison, and recovered-signer matching before broadcast. There is still no imported key, Sepolia adapter, hosted worker, or production key provider.
- SIWE messages, signatures, nonces, browser cookies, and Origin headers are untrusted authentication input. Weeks 15-16 fix one HTTPS relying party, canonical ERC-4361/EOA recovery, exact expiry, and durable one-way challenge use. Week 17 additionally requires a loopback peer and exact configured Origin, binds each nonce to a hashed 256-bit HttpOnly flow secret, and stores only hashes of bounded session/CSRF credentials. Relogin rotates an optional old session in the replacement transaction; logout requires fixed-time cookie/header CSRF equality and one-way revocation. Separate files/hosts do not coordinate, and a session grants no role or payment authority.
- HTTP bodies and headers are untrusted. Week 6 validates exact integer/address shapes, requires a bounded idempotency key, caps request bodies at 16 KiB, and returns non-leaking conflicts. Week 17 adds generic authentication failures, unique canonical cookie parsing, no-store responses, and exact Origin on authentication writes. Payment Intent endpoints still have no authorization or tenant boundary, and the whole API remains loopback/test-only.
- Database paths are operator-controlled configuration. Week 7 resolves one absolute path, runs known migrations before listening, rejects future schema versions, and uses parameterized SQL. A local database file remains mutable, unencrypted application data rather than a trust anchor.
- Pull-request code is untrusted input. CI has no deployment key, does not use `pull_request_target`, retains no checkout credentials, and receives only `contents: read` permission.
- Local SQLite files and logs do not provide production-grade confidentiality or tamper resistance.

## 4. Assets

| Asset | Consequence if compromised | Current treatment |
| --- | --- | --- |
| Test-wallet private key or mnemonic | Unauthorized signatures and loss of test assets | Week 14 generates an Anvil-only key inside the process, never imports/returns/logs/persists it, and best-effort zeroes the owned byte array; future Sepolia uses a separate isolated burner |
| Credential-bearing RPC URL | Quota theft and activity disclosure | Stored only in ignored local configuration; examples contain no credential |
| Signed raw transaction | Can be replayed while valid | Stored only in the local unencrypted lifecycle database for exact retry; snapshots, strings, boundary exceptions, and Week 14 harness output redact it; clean replay deletes its temporary database |
| SIWE nonce, plaintext, signature, and browser credentials | Captured material may replay a proof, hijack a session, or correlate an address | 128-bit server nonce; 256-bit flow/session/CSRF secrets; exact origin/URI/chain/statement/time; ERC-191 recovery; durable one-way challenge/flow use; hashed credentials; strict `__Host-` cookies; rotation; CSRF logout; generic failures; redacted strings. SQLite stores no plaintext message, signature, or raw bearer token |
| Chain, contract, and code-hash configuration | Wrong-chain execution or incorrect credit | Local syntax checks, chain/address/runtime matching, and Week 8 per-batch chain/Router policy exist; startup, trusted-block, and RPC-switch controls remain Gate B work |
| Payment intents, observations, checkpoints, ledger, finality, reconciliation reports, and transaction lifecycle rows | Duplicate value movement, stale qualification, lost entries, or unexplained differences | Separate strict schemas; atomic watermarked reads; exact caught-up snapshots; append-only histories; policy/source/unsigned fingerprints; linked reversals; strict retry verification; protocol finality, token delivery, balances, and tamper evidence remain Gates B/C/D |
| CI token and workflow | Repository or release-chain modification | Read-only permission, pinned Actions, and no persisted checkout credential |
| Dependency graph and build tools | Replaced or non-reproducible builds | Exact SDK/tool versions, NuGet locks, gitlinks, and verified archive hashes |

## 5. Security Invariants

Breaking any invariant requires the experiment to stop until it is investigated:

1. A production or mainnet key must never enter this repository, CI, logs, or test fixtures.
2. CI must not receive or persist signing secrets or deploy to a public network; its only key is generated in memory for a disposable Anvil process.
3. Anvil default accounts are local-only. Their keys are public and must never receive funds on a public network.
4. The only allowed public network is Sepolia. Deployment and signing entry points must reject chain ID `1`.
5. On-chain amounts use exact integer base units, never floating-point storage.
6. A transaction receipt or log does not imply finality and does not by itself authorize credit.
7. An unknown broadcast result must not create a new payment.
8. Logs must not contain a private key, mnemonic, credential-bearing RPC URL, or signed raw transaction.
9. An intent in `created` state must never be presented as wallet authorization, a transaction, chain observation, or settled funds.
10. An API process must not accept requests until every known database migration is applied; a newer unknown schema must fail closed.
11. An Indexer checkpoint is a restart cursor only; reorg recovery must be bounded and atomic, and no observed or locally canonical log may directly authorize credit or finality.
12. A provisional ledger effect records the consequence of local canonicality only; reversal must append and reference its earlier active effect, and neither entry may be presented as final settlement or payout authority.
13. Confirmation qualification must bind an exact Indexer snapshot to a fully caught-up Ledger cursor; qualification and later revocation are append-only policy evidence and must never be presented as protocol finality or settlement.
14. Reconciliation must bind exact Intent, Ledger, and Finality snapshots; a locally consistent report must never be presented as token delivery, accounting credit, payout authorization, or settlement.
15. A transaction operation must be durable before broadcast; an unknown result may only resend the same stored signed bytes, and a replacement may change fees but not chain, signer, nonce, destination, gas, value, or calldata.
16. Concrete signed bytes must round-trip to the approved type, hash, chain, signer, nonce, destination, fees, gas, zero value, access list, and calldata before broadcast.
17. A SIWE proof must match the configured relying party and exact issued challenge, recover the named EOA, remain unexpired, and consume its nonce atomically once; it must never authorize a payment or become a session by implication.
18. A browser session may be created only after the flow cookie is bound to the
    exact nonce and the SIWE proof succeeds; raw bearer/CSRF tokens must not be
    stored, replacement must not revoke the old session without creating the
    new one, and logout must require the session's matching CSRF cookie/header.

## 6. Risk Register

`Controlled` means that a testable Week 1 measure exists. `Planned` means that the measure must be delivered by the named gate. `Accepted` applies only to an explicit non-production residual risk.

| ID | Threat and impact | Likelihood | Impact | Control | Status | Target |
| --- | --- | --- | --- | --- | --- | --- |
| S01 | A key, signed transaction, or credential enters source, history, logs, or exception output | Medium | High | Ignore rules, working-tree/history scans, canary, redacted signed-payload strings, sanitized adapter-boundary exceptions, and a non-exporting generated Anvil key with best-effort zeroing; local lifecycle DB remains sensitive and unencrypted | Partly controlled | Gates A/D |
| S02 | A movable Action tag or replaced archive executes malicious build code | Low | High | Full Action commit SHAs; Foundry and Gitleaks archives use platform-specific SHA-256 | Controlled | Gate A |
| S03 | A pull request abuses a privileged token or secret | Medium | High | Read-only token, no persisted credential, no CI secret, no `pull_request_target` | Controlled | Gate A |
| S04 | A public Anvil default key is reused on Sepolia or mainnet | Medium | High | Explicit local-only boundary and a separate Sepolia burner | Controlled | Gate A |
| S05 | A malicious or incorrect RPC reports the wrong chain or contract state | Medium | High | Week 5 checks chain/address/runtime identity; Week 8 checks exact ranges, block identity/parents, emitter and event occurrence fields; trusted-block and independent-provider checks remain | Partly controlled | Gate B |
| S06 | Configuration error deploys or signs on an unintended network | Low | High | `DeployLocal` fails closed outside `31337`; the lifecycle policy allows only `31337` or `11155111`; the concrete generated-key/RPC adapter further requires credential-free loopback Anvil `31337`, its own signer, and the verified Router identity | Partly controlled | Gates A/B/D |
| S07 | Reorg, truncated logs, or incorrect finality causes false credit | Medium | High | Weeks 8-9 retain exact fork/canonicality history; Week 10 appends provisional reversals; Week 11 appends caught-up qualification/revocation; Week 12 appends a new discrepancy report after those changes; independent completeness checks, protocol finality, and authorization remain | Partly controlled | Gates B/C |
| S08 | Retry, concurrent nonce use, or unknown broadcast causes double payment | Medium | High | Week 7 deduplicates intents; Week 13 transactionally reserves shared-file nonces, persists raw/hash before broadcast, reuses exact bytes after unknown results, makes accepted evidence dominant, and restricts replacements to the same nonce/payment facts; Week 14 proves that path against real Anvil acceptance, duplicate import, replacement, receipt, and balances; cross-host coordination remains | Partly controlled | Gates B/D |
| S09 | SQLite data is modified and effects or transaction attempts become unexplainable | Medium | Medium | Versioned migrations, `STRICT`/`CHECK`/foreign-key/trigger constraints, append-only identities, linked reversals, evidence copies, source/policy/unsigned fingerprints, raw Keccak recomputation, and strict replay verification exist; backup, encryption, independent tamper evidence, and finalized balances remain | Partly controlled | Gates C/D |
| S10 | Secret scanning exits successfully while its rules are ineffective | Low | High | Fixed scanner version plus a dynamic canary with a dedicated expected exit code | Controlled | Gate A |
| S11 | An authentication or typed-data signature is replayed across relying parties, users, chains, browser contexts, or contracts | Medium | High | Weeks 15-16 fix SIWE origin/URI/statement/chain/time, nonce, canonical recovery, restart-safe consumption, and shared-file concurrency. Week 17 adds hashed browser binding, exact HTTP Origin, opaque session rotation, CSRF logout, one-way revocation, and restart tests. Cross-host coordination, rate limiting, production browser hosting, ERC-1271, and separate EIP-712/permit controls remain | Partly controlled | Gate E |
| S12 | A testnet RPC fails, test funds disappear, or test data is public | High | Low | Assign no value to test funds, store no customer data, and use Anvil for daily work | Accepted | Ongoing |
| S13 | API traffic grows databases, creates lock pressure, crosses tenants, or bypasses idempotency/session state through separate files | High | High | 16 KiB body limit, bounded keys/challenges/sessions, shared-file constraints, restart tests, loopback-only authentication, exact Origin, generic failures, and expiry exist. Payment Intents remain unauthorized; tenant scoping, quotas, rate limits, public hosting controls, and cross-host storage remain | Partly controlled | Gate B |
| S14 | A locally consistent reconciliation report is mistaken for settlement and triggers value movement | Medium | High | Multidimensional discrepancy model, immutable source coordinates/evidence, no source mutation or payout API, explicit `IsConsistent` boundary documentation, and reorg report-history test; accounting and authorization remain separate future controls | Partly controlled | Gate C |

## 7. Secret-Scanning Boundary

`.gitignore` reduces accidental additions but cannot remove a secret that already entered history. The scanning policy therefore:

- Extends the Gitleaks 8.29.1 defaults and adds rules for named 64-hex EVM private keys and plaintext mnemonic assignments.
- Uses a complete checkout in CI and scans all Git history.
- Also scans the current working tree in `verify.ps1`, including files not yet committed.
- Excludes only Git metadata, repository-local tools, pinned contract dependencies, and generated output.
- Creates a synthetic canary at runtime and requires the configured leak exit code. The complete canary value is never stored in the repository.

Any false-positive exception must be restricted to the exact rule and path after review. Broad directory exclusions, generic stop words, and allowlisting a real leaked value are prohibited.

## 8. Incident Response

If a possible secret or incorrect signature is found:

1. Stop related services, scripts, and broadcasts. Record the time, network, account, and recent operations.
2. Revoke or rotate the RPC token first. Treat a suspected wallet as compromised and move any remaining test assets.
3. Inspect logs, CI artifacts, forks, and remote mirrors to determine exposure.
4. Only after invalidating the secret, decide whether Git history must be rewritten. Rewriting history never replaces rotation.
5. Record the root cause, detection gap, and regression test before resuming the experiment.

If real funds, customer data, or an unclear jurisdiction is involved, stop immediately and escalate rather than treating the event as a routine learning failure.

## 9. Review Triggers

Review and version this document again no later than the first of these events:

- The first Sepolia deployment or signing path is introduced.
- Protocol-native finality, token-delivery evidence, accounting, a public-network/imported-key signer or broadcaster, production SIWE/browser hosting, authorization, or an API exposed beyond loopback becomes runnable.
- KMS, cloud hosting, a new RPC, another chain, or a third-party webhook is added.
- A secret-scan finding, supply-chain event, reorg failure, or funds anomaly occurs.
- Gate F release review starts.

Current evidence entry points are `.github/workflows/ci.yml`, `.gitleaks.toml`, `scripts/install-foundry.ps1`, and `scripts/verify.ps1`.
