# .NET EVM Payment Sandbox

A test-only learning and portfolio repository for building reliable EVM payment integrations from .NET.

> [!WARNING]
> This repository is not production-ready. It must not be used with mainnet, real funds, customer keys, or custody workflows. The current code provides local, test-only contract evidence and domain foundations; it does not implement a production payment service.

## Project status

**Current milestone:** Gate A accepted on 2026-08-28; Week 2 is next.

Gate A was scheduled across Weeks 1-4 and reached its bounded acceptance criteria early at commit [`cb5b5f6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/cb5b5f617828d14ea167fe0be4162f7d8f8f583e). Remote CI and an isolated Windows fresh clone both passed. The weekly learning sequence continues with Week 2 even though later Gate A code is already present.

Implemented in the accepted Gate A baseline:

- .NET SDK `10.0.400`, C# `14.0`, and Microsoft Testing Platform are pinned.
- NuGet versions are centrally managed and transitive dependency lock files are committed.
- `PaymentId` models a canonical, non-zero `bytes32` correlation identifier.
- `RawTokenAmount` models an exact, unsigned EVM `uint256` amount without floating-point conversion.
- xUnit v3 tests document and enforce those domain invariants.
- A stateless `PaymentRouter` transfers tokens directly from payer to merchant through `SafeERC20` and emits `PaymentRecorded` after success.
- `payWithPermit` supports a deliberately non-relayed ERC-2612 path where owner is `msg.sender`, spender is the Router, and permit value equals the payment amount.
- Six- and eighteen-decimal test tokens, a local deployment script, example-based tests, permit tests, fuzz tests, and invariant tests exercise the contract boundary.
- The Foundry toolchain is pinned to Solidity `0.8.36`, Prague EVM, OpenZeppelin Contracts `v5.7.0`, and forge-std `v1.16.1`.
- Local verification and remote CI check the locked .NET build/tests, Foundry formatting/build/tests, and committed secrets. Accepted CI run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588) passed all three jobs.

Deliberately not implemented yet:

- Nethereum RPC calls, generated contract bindings, or trusted-chain checks.
- Payment Intent API, database schema, indexer, ledger, or reconciliation.
- .NET transaction signing, broadcasting, nonce management, SIWE, or off-chain EIP-712/permit construction and validation.
- Production token allowlisting, fee-on-transfer/rebasing support, on-chain payment state, pause/admin/upgrade/rescue controls, or an audited deployment.
- Mainnet support, custody, production key management, or production operations.

## Windows quick start

### Prerequisites

- Windows 10 or later.
- Git with submodule support.
- Windows PowerShell 5.1 or PowerShell 7 (`pwsh`).
- .NET SDK `10.0.400`. The repository intentionally rejects a different SDK feature band.
- Internet access for dependency restore, the checksum-verified Foundry install, and the default Gitleaks scan.

### Clone and verify

```powershell
git clone --recurse-submodules https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox.git
Set-Location .\dotnet-evm-payment-sandbox

# Safe to repeat if the repository was cloned without --recurse-submodules.
git submodule update --init --recursive

dotnet --version
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-foundry.ps1 -AddToPath
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

The expected SDK output is `10.0.400`. With PowerShell 7, the script commands can instead use `pwsh -File ./scripts/install-foundry.ps1 -AddToPath` and `pwsh -File ./scripts/verify.ps1`. The verification script is the preferred local entry point because it mirrors the repository's CI checks and uses the repository-local Foundry binary directly.

To run the .NET checks directly:

```powershell
dotnet restore .\PaymentSandbox.slnx --locked-mode
dotnet build .\PaymentSandbox.slnx --configuration Release --no-restore
dotnet test .\PaymentSandbox.slnx --configuration Release --no-build --no-restore
```

To run the Foundry checks directly after installation:

```powershell
$forge = '.\.tools\foundry\v1.7.1\forge.exe'
& $forge fmt --root .\contracts --check
& $forge build --root .\contracts --sizes
& $forge test --root .\contracts -vvv
```

`scripts/verify.ps1 -SkipSecretScan` exists for an explicitly degraded offline run. Its warning is intentional: a run that skips secret scanning is not sufficient Gate A evidence.

Do not add a private key to `.env.example`, source files, command history, test fixtures, or CI variables. Gate A verification does not require any key.

## Repository map

| Path                                 | Current responsibility                                                                                                   |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------ |
| `global.json`                        | Selects the exact .NET SDK and Microsoft Testing Platform runner.                                                        |
| `Directory.Build.props`              | Applies common compiler, warning, deterministic-build, and lock-file rules.                                              |
| `Directory.Packages.props`           | Owns reviewed NuGet versions; project files do not choose versions.                                                      |
| `PaymentSandbox.slnx`                | Contains the current Domain and Domain test projects.                                                                    |
| `src/PaymentSandbox.Domain/`         | Pure domain values and invariants; no RPC, database, ASP.NET, or signer dependencies.                                    |
| `tests/PaymentSandbox.Domain.Tests/` | Executable specifications for the Domain project.                                                                        |
| `contracts/`                         | Independent Foundry workspace containing the test-only Router, test tokens, local deployment script, and contract tests. |
| `scripts/verify.ps1`                 | Runs the supported local verification sequence.                                                                          |
| `docs/architecture.md`               | Separates the accepted Gate A architecture from the planned payment flow.                                                |
| `docs/acceptance/gate-a.md`          | Records the Gate A criteria, observed runs, timing, and non-production boundary.                                         |
| `docs/threat-model.md`               | Records protected assets, trust boundaries, threats, and current controls.                                               |
| `docs/decisions/`                    | Records architectural decisions and their trade-offs.                                                                    |

## Current model

`PaymentId` is a public correlation value shared by future API, contract event, indexer, and persistence boundaries. It is random and non-zero, but it is not a secret, invoice number, authorization, or proof of payment. Repeated IDs must remain observable because partial and duplicate transfers are valid facts that later reconciliation must explain.

`RawTokenAmount` stores an ERC-20 amount in the token's smallest unit. It accepts values from zero through `2^256 - 1` and rejects values outside the EVM `uint256` range. Display decimals are intentionally absent so the core model cannot silently round a chain amount.

`PaymentRouter.pay` uses a prior allowance. `PaymentRouter.payWithPermit` creates an exact ERC-2612 allowance and transfers in the same transaction. The Router has no owner, storage variables, allowlist, pause switch, upgrade path, or withdrawal. Its payment functions reject the Router as merchant and are designed not to retain funds; unsolicited token transfers could still become permanently stuck. It also rejects a zero payment ID, zero/non-contract token, zero merchant, and zero amount.

The Router intentionally permits repeated `PaymentId` values. Partial, supplemental, excess, and accidental duplicate transfers must remain visible as separate chain events for the future indexer and reconciliation logic.

An ERC-2612 permit authorizes allowance only. In this sample it does not sign the merchant or payment ID and it does not support a relayer. The caller is therefore still the payer. A third party can submit a public permit directly to the token first and consume its nonce, causing this strict combined path to fail closed; this known denial-of-service limitation does not let that party choose the payment destination.

Gate A acceptance evidence on 2026-08-28 includes 15 passing .NET tests, 5 Foundry suites with 31 passed and no failed or skipped tests, a clean two-commit Git history scan, and a real Anvil `31337` broadcast of `PaymentRouter`, `TestUSDC`, and `TestToken18`. The isolated Windows sequence took 418.19 seconds (6 minutes 58.19 seconds), and remote CI run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588) passed all three jobs. Foundry `v1.7.1` produced a 1,030-byte Router runtime; fuzz cases ran 256 inputs each, and two invariants ran 64 runs by 2,048 calls with no reverts. See the [Gate A acceptance record](docs/acceptance/gate-a.md) for the measured steps and the initial CI failure that preceded acceptance.

## Architecture rules

- Domain code owns business meaning and invariants, not infrastructure concerns.
- Chain, RPC, persistence, and signing adapters will depend on Domain; Domain will not depend on them.
- The contract and .NET toolchains remain independently buildable and testable.
- Chain observations are not final settlement until later indexer/finality rules say so.
- A payment identifier correlates evidence; it never authorizes value movement.
- Raw on-chain values remain exact integers. Formatting is an edge concern.

See [Architecture](docs/architecture.md), the [Scope and boundaries ADR](docs/decisions/0001-scope-and-boundaries.md), and the [Gate A acceptance record](docs/acceptance/gate-a.md) for the rationale and evidence.

## Roadmap

| Stage              | Outcome                                                                                                                                                                             |
| ------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Gate A / Weeks 1-4 | **Accepted early on 2026-08-28:** foundations, `TestUSDC`, non-custodial `PaymentRouter`, failure/fuzz/invariant tests, local deployment, CI, and Windows fresh-clone verification. |
| Week 2 next        | Use the accepted Foundry workspace to study transactions, receipts, logs, gas, deployment, and revert behavior; acceptance does not remove the learning work.                       |
| Weeks 3-4          | Deepen review of the already-present Router and hardening evidence while preserving the accepted baseline.                                                                          |
| Weeks 5-12         | Add typed Nethereum access, Payment Intent API, reorg-safe indexing, append-only ledger, finality, and reconciliation.                                                              |
| Weeks 13-19        | Add a test-only transaction lifecycle orchestrator, SIWE, and separate EIP-712/permit replay controls.                                                                              |
| Weeks 20-24        | Add observability, fault tests, runbooks, security review, portfolio evidence, and a reproducible `v1.0.0` sample release.                                                          |

Each later capability must arrive with its failure cases and boundary documentation. A roadmap item is not an implemented feature.

## Production boundary

The accepted Gate A evidence demonstrates a bounded sample, not authorization to operate customer funds. Production use would require a separate threat model and review of custody, access control, key management, deployment ownership, monitoring, incident response, legal jurisdiction, dependency provenance, chain-specific finality, and operational limits.
