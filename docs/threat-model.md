# Threat Model v0

- Status: Week 1 security baseline
- Last updated: 2026-08-28
- Owner: repository maintainer

## 1. Purpose

This document identifies what the project currently protects, where trust boundaries exist, and which risks remain unresolved. It is not a security audit and does not show that the project is ready for production. Any change to the signing path, funds flow, network, or deployment boundary requires a matching threat-model review.

## 2. Scope

The current model covers:

- The developer workstation, Git working tree, and repository-local `.tools` directory.
- The GitHub repository and read-only GitHub Actions workflows.
- The .NET solution and the test-only PaymentRouter/TestUSDC implementation, including permit, fuzz, invariant, and local deployment tests.
- Local Anvil, plus one later smoke test on Ethereum Sepolia.
- The API, Indexer, SQLite database, Ledger, Orchestrator, and SIWE components planned for later gates.

The following are explicitly out of scope:

- Ethereum mainnet or any network that carries real value.
- Real customers, orders, production funds, custody, payouts, or yield claims.
- Production KMS, 24x7 operations, disaster recovery, multichain support, or a public API.
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
Payer -> Anvil/Sepolia -> untrusted RPC -> Indexer/API -> SQLite/Ledger
Test signing request -> Orchestrator -> isolated test wallet -> Anvil/Sepolia
```

Boundary assumptions:

- Download sites and third-party Actions are supply-chain boundaries. A version label alone is insufficient; Actions use commit SHAs and downloaded archives use fixed SHA-256 values.
- RPC output is untrusted. Later code must validate chain identity, trusted blocks, contract addresses, and code hashes, then account for reorgs and finality.
- Pull-request code is untrusted input. CI has no deployment key, does not use `pull_request_target`, retains no checkout credentials, and receives only `contents: read` permission.
- Local SQLite files and logs do not provide production-grade confidentiality or tamper resistance.

## 4. Assets

| Asset | Consequence if compromised | Current treatment |
| --- | --- | --- |
| Test-wallet private key or mnemonic | Unauthorized signatures and loss of test assets | Never committed, passed to CI, or logged; Sepolia uses an isolated burner |
| Credential-bearing RPC URL | Quota theft and activity disclosure | Stored only in ignored local configuration; examples contain no credential |
| Signed raw transaction | Can be replayed while valid | Not implemented before Gate D; later treated as sensitive and never logged |
| Chain, contract, and code-hash configuration | Wrong-chain execution or incorrect credit | Startup and RPC-switch validation planned for Gate B |
| Payment intents, events, and ledger | Duplicate credit, lost entries, or unexplained differences | Idempotency, reorg handling, append-only entries, and reconciliation planned for Gates B/C |
| CI token and workflow | Repository or release-chain modification | Read-only permission, pinned Actions, and no persisted checkout credential |
| Dependency graph and build tools | Replaced or non-reproducible builds | Exact SDK/tool versions, NuGet locks, gitlinks, and verified archive hashes |

## 5. Security Invariants

Breaking any invariant requires the experiment to stop until it is investigated:

1. A production or mainnet key must never enter this repository, CI, logs, or test fixtures.
2. CI must not hold signing material or deploy contracts or funds.
3. Anvil default accounts are local-only. Their keys are public and must never receive funds on a public network.
4. The only allowed public network is Sepolia. Deployment and signing entry points must reject chain ID `1`.
5. On-chain amounts use exact integer base units, never floating-point storage.
6. A transaction receipt or log does not imply finality and does not by itself authorize credit.
7. An unknown broadcast result must not create a new payment.
8. Logs must not contain a private key, mnemonic, credential-bearing RPC URL, or signed raw transaction.

## 6. Risk Register

`Controlled` means that a testable Week 1 measure exists. `Planned` means that the measure must be delivered by the named gate. `Accepted` applies only to an explicit non-production residual risk.

| ID | Threat and impact | Likelihood | Impact | Control | Status | Target |
| --- | --- | --- | --- | --- | --- | --- |
| S01 | A key enters source, history, or logs and exposes a wallet or RPC credential | Medium | High | Ignore rules, working-tree and history scans, Web3 rules, canary, redacted output | Controlled | Gate A |
| S02 | A movable Action tag or replaced archive executes malicious build code | Low | High | Full Action commit SHAs; Foundry and Gitleaks archives use platform-specific SHA-256 | Controlled | Gate A |
| S03 | A pull request abuses a privileged token or secret | Medium | High | Read-only token, no persisted credential, no CI secret, no `pull_request_target` | Controlled | Gate A |
| S04 | A public Anvil default key is reused on Sepolia or mainnet | Medium | High | Explicit local-only boundary and a separate Sepolia burner | Controlled | Gate A |
| S05 | A malicious or incorrect RPC reports the wrong chain or contract state | Medium | High | Validate chain ID, trusted block, contract address, and code hash; cross-check critical data | Planned | Gate B |
| S06 | Configuration error deploys or signs on mainnet | Low | High | `DeployLocal` already fails closed outside chain ID `31337`; future signing and Sepolia entry points must use an explicit allowlist and reject chain ID `1` | Partly controlled | Gates A/B |
| S07 | Reorg, truncated logs, or incorrect finality causes false credit | Medium | High | Canonical blocks, common-ancestor rollback, finality anchors, and fault tests | Planned | Gates B/C |
| S08 | Retry, concurrent nonce use, or unknown broadcast causes double payment | Medium | High | Business idempotency, nonce coordination, persisted raw transaction/hash, same-payload rebroadcast | Planned | Gate D |
| S09 | SQLite or ledger data is modified and balances become unexplainable | Medium | Medium | Append-only entries, unique sources, reversals, and block-bounded reconciliation | Planned | Gate C |
| S10 | Secret scanning exits successfully while its rules are ineffective | Low | High | Fixed scanner version plus a dynamic canary with a dedicated expected exit code | Controlled | Gate A |
| S11 | Typed-data authorization is replayed across users, chains, or contracts | Medium | High | Domain, chain ID, verifying contract, nonce, deadline, and concurrent-consumption tests | Planned | Gate E |
| S12 | A testnet RPC fails, test funds disappear, or test data is public | High | Low | Assign no value to test funds, store no customer data, and use Anvil for daily work | Accepted | Ongoing |

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

Upgrade this document to v1 no later than the first of these events:

- The first Sepolia deployment or signing path is introduced.
- The API, Indexer, Ledger, or Orchestrator becomes runnable.
- KMS, cloud hosting, a new RPC, another chain, or a third-party webhook is added.
- A secret-scan finding, supply-chain event, reorg failure, or funds anomaly occurs.
- Gate F release review starts.

Current evidence entry points are `.github/workflows/ci.yml`, `.gitleaks.toml`, `scripts/install-foundry.ps1`, and `scripts/verify.ps1`.
