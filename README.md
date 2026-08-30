# .NET EVM Payment Sandbox

A test-only learning and portfolio repository for building reliable EVM payment integrations from .NET.

> [!WARNING]
> This repository is not production-ready. It must not be used with mainnet, real funds, customer keys, or custody workflows. The current code provides local, test-only contract, Payment Intent API, and bounded chain-observation evidence; it does not implement a production payment service.

## Project status

**Current milestone:** Gate A accepted on 2026-08-28; Weeks 2-12 complete; Week 13 is next.

Gate A was scheduled across Weeks 1-4 and reached its bounded acceptance criteria early at commit [`cb5b5f6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/cb5b5f617828d14ea167fe0be4162f7d8f8f583e). Remote CI and an isolated Windows fresh clone both passed. Week 2 added executable transaction observation, Week 3 deepened Router behavior evidence, Week 4 made the reviewed contract/interface baseline and clean tracked-source replay machine-checkable, Week 5 introduced the first narrow .NET contract adapter, Week 6 added the first runnable off-chain API boundary, Week 7 made its intent state durable, Week 8 added bounded block/log observation with a durable restart cursor, Week 9 added bounded fork recovery with append-only canonicality history, Week 10 projects that history into append-only provisional effects and explicit reversals, Week 11 adds reversible confirmation-depth qualification over exact caught-up source snapshots, and Week 12 appends explainable per-payment reconciliation reports over atomic Intent/Ledger/Finality snapshots.

Implemented in the current repository:

- .NET SDK `10.0.400`, C# `14.0`, and Microsoft Testing Platform are pinned.
- NuGet versions are centrally managed and transitive dependency lock files are committed.
- `PaymentId` models a canonical, non-zero `bytes32` correlation identifier.
- `RawTokenAmount` models an exact, unsigned EVM `uint256` amount without floating-point conversion.
- xUnit v3 tests document and enforce those domain invariants.
- A stateless `PaymentRouter` transfers tokens directly from payer to merchant through `SafeERC20` and emits `PaymentRecorded` after success.
- `payWithPermit` supports a deliberately non-relayed ERC-2612 path where owner is `msg.sender`, spender is the Router, and permit value equals the payment amount.
- Six- and eighteen-decimal test tokens, a local deployment script, example-based tests, permit tests, fuzz tests, and invariant tests exercise the contract boundary.
- A fee-on-transfer test fixture proves that `SafeERC20` success and `PaymentRecorded.amount` do not guarantee the merchant's exact balance delta.
- A committed Router ABI and reviewed baseline pin selectors, event topic, empty storage layout, compile settings, dependency commits, runtime size, and runtime code hash.
- `PaymentSandbox.Contracts` maps the reviewed ABI to typed Nethereum functions, event, and errors while keeping Nethereum out of Domain.
- A fail-closed connector validates operator configuration, `eth_chainId`, the configured Router address, and `eth_getCode` runtime Keccak before exposing a local calldata encoder.
- The public Contracts API has no account, signer, broadcast, receipt-polling, or settlement method; its RPC surface is limited to two identity observations.
- `PaymentSandbox.Api` creates and queries durable local Payment Intents through real HTTP endpoints without contacting RPC or generating a transaction.
- Create requests use exact string-encoded chain IDs and raw amounts, canonical addresses, and atomic business idempotency under concurrent retries.
- First creation returns `201`; a semantically identical replay returns the original resource with `200`; conflicting key reuse returns a non-leaking `409`.
- A versioned SQLite `STRICT` schema, unique binary idempotency key, and insert-first transaction preserve intents across restarts and coordinate processes sharing one database file.
- `PaymentSandbox.Indexer` scans caller-selected exact block ranges, validates chain ID, block hashes/parents, Router event occurrence fields, and persists append-only observations with an atomic checkpoint.
- A boundary parent mismatch starts a bounded common-ancestor search; one atomic transaction retains the old fork, appends detach/attach canonicality transitions, and switches the checkpoint to a validated replacement branch.
- Indexer retries verify every durable row and transition; concurrent same-range or same-reorg scanners commit once and replay once, while a changed cursor, over-depth fork, or inconsistent in-range parent fails closed.
- `PaymentSandbox.Ledger` consumes an explicit committed canonicality high-watermark through a read-only Indexer interface and writes to its own migration-owned SQLite database.
- Each canonical payment occurrence appends a provisional effect; a later noncanonical transition appends a reversal linked to the active effect. Neither path deletes source evidence, mutates a Payment Intent, or claims finality.
- Ledger source checkpoints, versioned SHA-256 source-fact fingerprints, unique entries, and atomic transactions make lost-response and concurrent same-batch retries exactly replayable while changed source facts fail as conflicts.
- `PaymentSandbox.Finality` atomically binds one Indexer head/transition snapshot to a fully caught-up Ledger checkpoint and exact Ledger entry high-watermark.
- A named confirmation-depth policy appends `confirmation_qualified`; head regression or a Ledger reversal appends a linked `confirmation_revoked`. No mutable finality flag or historical overwrite is used.
- Finality policy meaning, source snapshots, copied Ledger facts, decisions, and checkpoints are constrained and fingerprinted in a third independently migrated SQLite database.
- `PaymentSandbox.Reconciliation` compares one explicit `PaymentId` across atomically watermarked Intent, Ledger, and Finality reads without mutating any source.
- Reconciliation separately records active/matching/qualified occurrence counts, matching and qualified amounts, and stable discrepancy codes for missing intents/payments, reversals, term mismatches, under/overpayment, and incomplete qualification.
- Reports and complete selected Ledger/Finality evidence are append-only in another migration-owned SQLite database; exact retries verify every durable field while changed facts at the same coordinates fail closed.
- Verification replays compilation, local deployment, successful payment, and revert from a disposable directory containing only Git-known source and the two direct contract dependencies.
- The Foundry toolchain is pinned to Solidity `0.8.36`, Prague EVM, OpenZeppelin Contracts `v5.7.0`, and forge-std `v1.16.1`.
- Local verification and remote CI check the locked .NET build/tests, Foundry formatting/build/tests, local RPC observation, and committed secrets. Gate A run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588), Week 2 run [`33102551138`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33102551138), Week 3 run [`33127124223`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33127124223), Week 4 run [`33254032343`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33254032343), Week 5 run [`33257669877`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33257669877), Week 6 run [`33259846122`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33259846122), Week 7 run [`33262105541`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33262105541), Week 8 run [`33263968803`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33263968803), and Week 9 run [`33265607326`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33265607326) each passed all three jobs at their respective milestones.
- The Week 2 observer owns a disposable Anvil process, deploys through an unlocked local account, and machine-checks transaction calldata, receipts, gas, `PaymentRecorded`, balances, nonce consumption, and reverted-transaction postconditions without reading a private key.

Deliberately not implemented yet:

- Cross-host database coordination, backup/encryption/tamper evidence, finalized balances, accounting journals, or settlement authorization.
- API authentication, authorization, tenant isolation, rate limiting, public hosting, or production data handling.
- Indexer/Ledger/Finality/Reconciliation hosting or scheduling, protocol-native finalized block proofs, application startup wiring, deployment registry, trusted-block/cross-provider checks, completeness proofs, or a public-network Router address.
- .NET transaction signing, broadcasting, nonce management, SIWE, or off-chain EIP-712/permit construction and validation.
- Production token allowlisting, fee-on-transfer/rebasing support, on-chain payment state, pause/admin/upgrade/rescue controls, or an audited deployment.
- Mainnet support, custody, production key management, or production operations.

## Windows quick start

### Prerequisites

- Windows 10 or later.
- Git with submodule support.
- PowerShell 7 (`pwsh`). The Week 2 observer relies on its cross-platform process and JSON behavior.
- .NET SDK `10.0.400`. The repository intentionally rejects a different SDK feature band.
- Internet access for dependency restore, the checksum-verified Foundry install, and the default Gitleaks scan.

### Clone and verify

```powershell
git clone https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox.git
Set-Location .\dotnet-evm-payment-sandbox

# Only these two direct dependencies are required. Their optional nested test
# submodules are deliberately outside the PaymentRouter build boundary.
git submodule update --init -- contracts/lib/openzeppelin-contracts contracts/lib/forge-std

dotnet --version
pwsh -NoProfile -File .\scripts\install-foundry.ps1 -AddToPath
pwsh -NoProfile -File .\scripts\verify.ps1
```

The expected SDK output is `10.0.400`. The verification script is the preferred local entry point because it mirrors the repository's CI checks and uses the repository-local Foundry binaries directly. It rejects ABI, bytecode, version, storage, or dependency drift; creates a temporary tracked-source snapshot; and starts and tears down a temporary Anvil process on port `18545`. It refuses to reuse an occupied port.

To run the .NET checks directly:

```powershell
dotnet restore .\PaymentSandbox.slnx --locked-mode
dotnet build .\PaymentSandbox.slnx --configuration Release --no-restore
dotnet test .\PaymentSandbox.slnx --configuration Release --no-build --no-restore
```

To run the local-only API:

```powershell
dotnet run --project .\src\PaymentSandbox.Api --urls http://127.0.0.1:5086
```

The default SQLite database is created under
`src/PaymentSandbox.Api/data/payment-intents.db` and survives restart. Processes
coordinate only when configured to use the same local file. Keep this endpoint
on loopback; it has no authentication or production abuse controls.

To run the Foundry checks directly after installation:

```powershell
$forge = '.\.tools\foundry\v1.7.1\forge.exe'
& $forge fmt --root .\contracts --check
& $forge build --root .\contracts --sizes
pwsh -NoProfile -File .\scripts\verify-contract-baseline.ps1
& $forge test --root .\contracts -vvv
pwsh -NoProfile -File .\scripts\verify-clean-contract-deployment.ps1 -Port 19545
```

`scripts/verify.ps1 -SkipSecretScan` exists for an explicitly degraded offline run. Its warning is intentional: a run that skips secret scanning is not complete milestone evidence.

Do not add a private key to `.env.example`, source files, command history, test fixtures, or CI variables. Gate A verification does not require any key.

## Repository map

| Path                                       | Current responsibility                                                                                                   |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------ |
| `global.json`                              | Selects the exact .NET SDK and Microsoft Testing Platform runner.                                                        |
| `Directory.Build.props`                    | Applies common compiler, warning, deterministic-build, and lock-file rules.                                              |
| `Directory.Packages.props`                 | Owns reviewed NuGet versions; project files do not choose versions.                                                      |
| `PaymentSandbox.slnx`                      | Contains the Domain, Contracts, API, and Indexer projects with their test projects.                                      |
| `src/PaymentSandbox.Domain/`               | Pure domain values and invariants; no RPC, database, ASP.NET, or signer dependencies.                                    |
| `tests/PaymentSandbox.Domain.Tests/`       | Executable specifications for the Domain project.                                                                        |
| `src/PaymentSandbox.Contracts/`            | Typed Router ABI projection, read-only identity RPC adapter, trust policy, and verified local calldata encoder.          |
| `tests/PaymentSandbox.Contracts.Tests/`    | Network-free identity, failure-boundary, ABI selector, event-indexing, and calldata tests.                               |
| `src/PaymentSandbox.Api/`                  | Runnable local Payment Intent HTTP boundary, versioned SQLite migration, and durable idempotent store.                   |
| `tests/PaymentSandbox.Api.Tests/`          | Service, migration, constraint, restart, and real loopback-Kestrel concurrency tests.                                    |
| `src/PaymentSandbox.Indexer/`              | Exact-range RPC observation, bounded fork recovery, append-only canonicality history, and checkpoint transactions.       |
| `tests/PaymentSandbox.Indexer.Tests/`      | Model, raw JSON-RPC/ABI, migration, retry, concurrency, resource-bound, and synthetic-fork tests.                         |
| `contracts/`                               | Independent Foundry workspace containing the test-only Router, test tokens, local deployment script, and contract tests. |
| `contracts/abi/PaymentRouter.json`         | Reviewed standard ABI array for later typed client generation.                                                          |
| `contracts/baselines/PaymentRouter.v1.json` | Reviewed toolchain, selector, storage-layout, size, and runtime-code identity.                                         |
| `scripts/verify.ps1`                       | Runs the supported local verification sequence.                                                                          |
| `scripts/verify-contract-baseline.ps1`     | Recompiles and compares the reviewed Router ABI, versions, storage, and bytecode evidence.                               |
| `scripts/verify-clean-contract-deployment.ps1` | Replays deployment and transaction evidence from an isolated Git-known source snapshot.                            |
| `scripts/observe-week2-transaction.ps1`    | Runs the disposable Week 2 Anvil transaction/receipt/log/gas observation and its assertions.                             |
| `docs/architecture.md`                     | Separates the accepted Gate A architecture from the planned payment flow.                                                |
| `docs/acceptance/gate-a.md`                | Records the Gate A criteria, observed runs, timing, and non-production boundary.                                         |
| `docs/learning/week-02-evm-observation.md` | Explains how to read the observer's transactions, receipts, logs, gas, nonce, revert, and finality evidence.             |
| `docs/learning/week-03-payment-router-v1.md` | Explains Router execution order, atomic units, allowance, event meaning, repeated IDs, and token semantic risks.       |
| `docs/learning/week-04-contract-hardening.md` | Explains the reviewed ABI/runtime baseline, stronger properties, clean replay, and update procedure.                  |
| `docs/learning/week-05-contract-adapter.md` | Explains typed binding, fail-closed RPC identity checks, calldata encoding, and remaining trust limits.                |
| `docs/learning/week-06-payment-intent-api.md` | Explains HTTP contracts, normalized idempotency, concurrency, and volatile-store limits.                             |
| `docs/learning/week-07-sqlite-persistence.md` | Explains schema ownership, insert-first transactions, restart evidence, and SQLite limits.                           |
| `docs/learning/week-08-chain-observation-checkpoints.md` | Explains exact-range observation, occurrence identity, atomic checkpoints, and the current reorg boundary.    |
| `docs/threat-model.md`                     | Records protected assets, trust boundaries, threats, and current controls.                                               |
| `docs/decisions/`                          | Records architectural decisions and their trade-offs.                                                                    |

## Current model

`PaymentId` is a public correlation value shared by future API, contract event, indexer, and persistence boundaries. It is random and non-zero, but it is not a secret, invoice number, authorization, or proof of payment. Repeated IDs must remain observable because partial and duplicate transfers are valid facts that later reconciliation must explain.

`RawTokenAmount` stores an ERC-20 amount in the token's smallest unit. It accepts values from zero through `2^256 - 1` and rejects values outside the EVM `uint256` range. Display decimals are intentionally absent so the core model cannot silently round a chain amount.

`EvmChainId`, `EvmAddress`, `PaymentIntentTerms`, and `PaymentIntent` add the
off-chain creation model. `created` means only that the current API process
accepted the immutable terms; it is not wallet authorization or chain progress.

`PaymentRouter.pay` uses a prior allowance. `PaymentRouter.payWithPermit` creates an exact ERC-2612 allowance and transfers in the same transaction. The Router has no owner, storage variables, allowlist, pause switch, upgrade path, or withdrawal. Its payment functions reject the Router as merchant and are designed not to retain funds; unsolicited token transfers could still become permanently stuck. It also rejects a zero payment ID, zero/non-contract token, zero merchant, and zero amount.

The Router intentionally permits repeated `PaymentId` values. Partial, supplemental, excess, and accidental duplicate transfers must remain visible as separate chain events for the future indexer and reconciliation logic.

An ERC-2612 permit authorizes allowance only. In this sample it does not sign the merchant or payment ID and it does not support a relayer. The caller is therefore still the payer. A third party can submit a public permit directly to the token first and consume its nonce, causing this strict combined path to fail closed; this known denial-of-service limitation does not let that party choose the payment destination.

Gate A acceptance evidence on 2026-08-28 includes 15 passing .NET tests, 5 Foundry suites with 31 passed and no failed or skipped tests, a clean two-commit Git history scan, and a real Anvil `31337` broadcast of `PaymentRouter`, `TestUSDC`, and `TestToken18`. The isolated Windows sequence took 418.19 seconds (6 minutes 58.19 seconds), and remote CI run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588) passed all three jobs. Foundry `v1.7.1` produced a 1,030-byte Router runtime; fuzz cases ran 256 inputs each, and two invariants ran 64 runs by 2,048 calls with no reverts. See the [Gate A acceptance record](docs/acceptance/gate-a.md) for the measured steps and the initial CI failure that preceded acceptance.

Week 2 adds live JSON-RPC evidence on a script-owned Anvil chain. The successful path proves exact calldata, a `status = 1` receipt, the decoded Router event, the token balance delta, and zero Router custody. The explicit-gas failure path proves a mined `status = 0` receipt consumes gas and one account nonce while retaining no logs or token balance changes. Commit [`19e61c5`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/19e61c532bb557fe91b27f88cde7b3ff1df30b56) and CI run [`33102551138`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33102551138) record the implementation and its cross-platform verification. These local receipts are observations, not finality or production settlement.

Week 3 makes the Router v1 boundary explicit. Exact allowance assertions show which state belongs to the token and that reverts restore it. A deliberately unsupported fee-on-transfer fixture returns success while delivering less than the Router event's requested amount, proving that `SafeERC20` normalizes call behavior rather than token economics. Commit [`9daef77`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/9daef77653218ebe826d489312c2f8fc8d3c6c8a) and CI run [`33127124223`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33127124223) record the implementation and its cross-platform verification. See the [Week 3 Router guide](docs/learning/week-03-payment-router-v1.md) for the evidence matrix and precise non-custodial claim.

Week 4 freezes a reviewed v1 consumer contract without changing `PaymentRouter` source. The committed ABI is regenerated and structurally compared; compile settings, direct dependency commits, selectors, event topic, empty storage layout, 1,030-byte runtime, and runtime Keccak are checked independently. Fuzz and invariant evidence now covers random same-ID partitions, arbitrary insufficient-balance rollback, balance/supply conservation, and unlimited allowance behavior. The deployment observation runs from an isolated Git-known source snapshot so ignored artifacts cannot satisfy the check. Commit [`c146420`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/c146420c837b0ab5127595377b7a9dbd372b942c) and CI run [`33254032343`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33254032343) record the implementation and cross-platform evidence. See the [Week 4 hardening guide](docs/learning/week-04-contract-hardening.md).

Week 5 adds the first Nethereum boundary without adding a runtime payment service. A narrow RPC interface observes only chain ID and latest deployed code. The connector validates those observations against an operator-reviewed policy and returns a `VerifiedPaymentRouterClient` only after the runtime Keccak matches. That client generates unsigned `pay` and `payWithPermit` calldata from Domain values; it cannot sign or send. The check constrains accidental wrong-chain/address/code use but still trusts one RPC endpoint and is neither finality nor proof of an honest provider. Commit [`9969cd6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/9969cd6) and CI run [`33257669877`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33257669877) record the implementation and cross-platform evidence. See the [Week 5 contract adapter guide](docs/learning/week-05-contract-adapter.md).

Week 6 adds a runnable ASP.NET Core boundary for creating and querying Payment
Intents. The API normalizes exact business terms before comparing idempotent
retries and atomically publishes its key and payment-ID indexes. At the Week 6
milestone the store was intentionally volatile and single-process; the API has
no RPC, signing, broadcasting, indexing, or settlement capability. See the [Week 6
Payment Intent guide](docs/learning/week-06-payment-intent-api.md).

The 2026-08-29 Week 6 local verification passed 85 .NET tests, all 36 unchanged
Foundry tests, the reviewed contract baseline, isolated successful/reverted
Anvil observation, the dynamic secret canary, and working-tree/full-history
secret scans. A real loopback HTTP smoke test also produced `201` create, `200`
safe replay, and `200` query responses. Implementation commit
[`4ffb8ba`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/4ffb8ba)
and CI run [`33259846122`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33259846122)
record the cross-platform evidence; all three jobs passed in 32 seconds.

Week 7 replaces the dictionary with `Microsoft.Data.Sqlite`. An application-owned
migration creates `STRICT` tables, while a binary unique key and insert-first
transaction preserve the existing create/replay/conflict contract across
restart and processes sharing one local file. Startup fails closed on an
unsupported schema version. This adds durability, not payment settlement or a
production database security posture. See the [Week 7 SQLite guide](docs/learning/week-07-sqlite-persistence.md).

The 2026-08-30 Week 7 local verification passed 93 .NET tests, all 36 unchanged
Foundry tests, the reviewed contract baseline, isolated successful/reverted
Anvil observation, the dynamic secret canary, and working-tree/full-history
secret scans. A real stop/start smoke test then queried and safely replayed the
same durable Payment Intent and PaymentId from the default SQLite file.
Implementation commit
[`824bc4a`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/824bc4a)
and CI run [`33262105541`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33262105541)
record the cross-platform evidence; all three jobs passed.

Week 8 adds a separate `PaymentSandbox.Indexer` batch boundary. It reads only an
explicit caller-selected block range, checks chain identity and parent-linked
block headers, decodes the reviewed Router event, and atomically stores blocks,
event occurrences, and a revisioned checkpoint. The checkpoint is a restart
cursor, not a canonicality, confirmation, finality, credit, or settlement claim.
Parent mismatch stops the scan; common-ancestor recovery and reversals are not
implemented yet. See the [Week 8 observation guide](docs/learning/week-08-chain-observation-checkpoints.md).

The 2026-08-30 Week 8 local verification passed 126 .NET tests, including the
33-test focused Indexer suite, all 36 unchanged Foundry tests, the reviewed
contract baseline, and successful/reverted Anvil observation replay from 1,097
Git-known files. The dynamic secret canary and working-tree/full-history scans
also passed across the current 15-commit history.
Implementation commit
[`263288d`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/263288d)
and CI run [`33263968803`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33263968803)
record the cross-platform evidence; all three jobs passed.

Week 9 keeps the Week 8 source observations immutable while adding an explicit
current-chain interpretation. A parent mismatch at the durable boundary triggers
a bounded backward comparison of RPC blocks with locally canonical occurrences.
After a common ancestor is proven, one transaction appends `noncanonical`
transitions for the detached suffix, stores and marks the replacement suffix
`canonical`, and revision-guards the checkpoint switch. A mismatch inside a
freshly read range, a missing ancestor, or an over-depth fork still fails without
durable changes. This local canonical label is neither confirmation nor finality
and never changes an Intent or credits a merchant. See the [Week 9 fork recovery
guide](docs/learning/week-09-reorg-canonicality.md).

The 2026-08-30 Week 9 local verification passed 132/132 .NET tests, including
the 39-test focused Indexer suite, all 36 unchanged Foundry tests, the reviewed
Router baseline, and successful/reverted Anvil observation replay from 1,097
Git-known files. The dynamic secret canary and working-tree/full-history scans
also passed across the current 17-commit history.
Implementation commit
[`ed58dd5`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/ed58dd575bbf3f0b3b974395f4d0d2d0a9619957)
and CI run [`33265607326`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33265607326)
record the cross-platform evidence; all three jobs passed.

Week 10 adds a separate `PaymentSandbox.Ledger` class library and SQLite schema.
Its processor consumes only caller-selected canonicality transition IDs through
the Indexer's read-only append-log interface. A canonical block occurrence adds
a `canonical_payment` entry; a later detach adds a
`canonical_payment_reversal` that references the earlier active entry. A second
canonical transition after reversal starts a new effect generation. The source
cursor and entries commit atomically, and the batch fingerprint includes the
ordered source facts but deliberately excludes local recording time so an
unknown-result retry can prove an exact replay. These are provisional branch
effects, not confirmation, finality, balances, payout authorization, or token
delivery evidence. See the [Week 10 ledger guide](docs/learning/week-10-reversible-ledger.md).

The 2026-08-30 Week 10 local verification passed 152/152 .NET tests, including
the 40-test focused Indexer suite and 19-test focused Ledger suite, all 36
unchanged Foundry tests, the reviewed Router baseline, and successful/reverted
Anvil observation replay from 1,126 Git-known files. The dynamic secret canary
and working-tree/full-history scans also passed across the complete 20-commit
history. Implementation commit
[`60643db`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/60643db)
records the Week 10 boundary. GitHub Actions run
[`33268538834`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33268538834)
passed its locked .NET, Foundry/RPC, and secret-scan jobs.

Week 11 adds `PaymentSandbox.Finality` without changing provisional Ledger rows.
The caller selects an exact Ledger entry high-watermark and atomically read
Indexer snapshot. Evaluation fails unless Ledger has consumed precisely that
snapshot's canonicality transition watermark. An active effect qualifies when
`head - effectBlock + 1` reaches the named policy threshold; a Ledger reversal
or a head below that threshold appends a revocation linked to the earlier
qualification. This is a reversible local confirmation policy result, not
protocol finality, settlement, token-delivery proof, or payout authorization.
See the [Week 11 confirmation guide](docs/learning/week-11-confirmation-finality.md).

The 2026-08-30 Week 11 local verification passed 171/171 .NET tests, including
the 40-test focused Indexer suite, 20-test focused Ledger suite, and 18-test
focused Finality suite. All 36 unchanged Foundry tests, the 1,030-byte/zero-slot
Router baseline, and successful/reverted Anvil observation replay from 1,152
Git-known files also passed. The dynamic secret canary and working-tree/full-
history scans passed across the complete 23-commit history. Implementation
commit [`f8c8a69`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/f8c8a69)
records the Week 11 boundary. GitHub Actions run
[`33270360367`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33270360367)
passed its locked .NET, Foundry/RPC, and secret-scan jobs.

Week 12 adds `PaymentSandbox.Reconciliation` and a fourth projection database.
The caller selects one `PaymentId` plus exact Intent publication, Ledger entry,
and Finality transition watermarks. Reconciliation refuses stale or uncaught-up
sources, then derives independent counts and amounts instead of a mutable
`paid` flag. Partial and supplemental occurrences aggregate; excess value,
wrong token/merchant/chain, missing or reversed payment history, and incomplete
qualification remain explicit discrepancy codes. Each evaluation copies its
selected evidence and appends a new report. This is local evidence agreement,
not token-delivery proof, accounting, protocol finality, or settlement. See the
[Week 12 reconciliation guide](docs/learning/week-12-reconciliation.md).

The 2026-08-30 Week 12 committed-snapshot verification passed 193/193 .NET
tests, including 20/20 focused Reconciliation tests. All 36 unchanged Foundry
tests, the 1,030-byte/zero-slot Router baseline, and successful/reverted Anvil
observation replay from 1,177 Git-known files also passed. The dynamic secret
canary and working-tree/full-history scans passed across the complete 26-commit
history. Implementation commit
[`cb1131b`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/cb1131b)
records the Week 12 boundary. GitHub Actions run
[`33289914693`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33289914693)
passed its locked .NET, Foundry/RPC, and secret-scan jobs.

## Architecture rules

- Domain code owns business meaning and invariants, not infrastructure concerns.
- Chain, RPC, persistence, and signing adapters depend on Domain; Domain does not depend on them.
- The contract and .NET toolchains remain independently buildable and testable.
- Chain observations are not final settlement until later indexer/finality rules say so.
- A payment identifier correlates evidence; it never authorizes value movement.
- Raw on-chain values remain exact integers. Formatting is an edge concern.
- Contract-baseline drift requires explicit interface, bytecode, dependency, and downstream-consumer review.

See [Architecture](docs/architecture.md), the [Scope and boundaries ADR](docs/decisions/0001-scope-and-boundaries.md), the [Gate A acceptance record](docs/acceptance/gate-a.md), and the [Week 2](docs/learning/week-02-evm-observation.md), [Week 3](docs/learning/week-03-payment-router-v1.md), [Week 4](docs/learning/week-04-contract-hardening.md), [Week 5](docs/learning/week-05-contract-adapter.md), [Week 6](docs/learning/week-06-payment-intent-api.md), [Week 7](docs/learning/week-07-sqlite-persistence.md), [Week 8](docs/learning/week-08-chain-observation-checkpoints.md), [Week 9](docs/learning/week-09-reorg-canonicality.md), [Week 10](docs/learning/week-10-reversible-ledger.md), [Week 11](docs/learning/week-11-confirmation-finality.md), and [Week 12](docs/learning/week-12-reconciliation.md) learning guides for the rationale and evidence.

## Roadmap

| Stage              | Outcome                                                                                                                                                                             |
| ------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Gate A / Weeks 1-4 | **Accepted early on 2026-08-28:** foundations, `TestUSDC`, non-custodial `PaymentRouter`, failure/fuzz/invariant tests, local deployment, CI, and Windows fresh-clone verification. |
| Week 2             | **Complete:** executable Anvil observation of successful and reverted transactions, receipts, logs, nonce, exact balances, and gas cost.                                            |
| Week 3             | **Complete:** Router v1 execution/allowance/event evidence, repeated and partial payments, and an executable fee-on-transfer limitation.                                             |
| Week 4             | **Complete:** reviewed ABI/runtime/version/storage baseline, stronger fuzz/invariants, and clean tracked-source deployment replay.                                                    |
| Week 5             | **Complete:** typed Nethereum ABI access, fail-closed chain/address/runtime-code identity checks, and unsigned local calldata encoding.                                               |
| Week 6             | **Complete:** runnable create/query Payment Intent API with normalized, concurrent-safe process-local idempotency.                                                                    |
| Week 7             | **Complete:** migration-owned SQLite persistence, restart-safe intents, schema constraints, and durable atomic idempotency.                                                           |
| Week 8             | **Complete:** exact-range chain/log validation, append-only SQLite observations, atomic restart checkpoint, and fork-stop behavior without finality claims.                         |
| Week 9             | **Complete:** bounded common-ancestor recovery, retained fork evidence, append-only canonicality transitions, and atomic checkpoint switching.                                      |
| Week 10            | **Complete:** independent append-only provisional ledger, linked reorg reversals, source checkpoint/fingerprint idempotency, and cross-database integration evidence.              |
| Week 11            | **Complete:** named confirmation-depth policy, exact caught-up Indexer/Ledger snapshots, and append-only qualification/revocation history.                                         |
| Week 12            | **Complete:** exact Intent/Ledger/Finality snapshots, append-only per-payment reports, evidence copies, and explainable discrepancy codes.                                          |
| Weeks 13-19 next   | Add a test-only transaction lifecycle orchestrator, SIWE, and separate EIP-712/permit replay controls.                                                                              |
| Weeks 20-24        | Add observability, fault tests, runbooks, security review, portfolio evidence, and a reproducible `v1.0.0` sample release.                                                          |

Each later capability must arrive with its failure cases and boundary documentation. A roadmap item is not an implemented feature.

## Production boundary

The accepted Gate A evidence demonstrates a bounded sample, not authorization to operate customer funds. Production use would require a separate threat model and review of custody, access control, key management, deployment ownership, monitoring, incident response, legal jurisdiction, dependency provenance, chain-specific finality, and operational limits.
