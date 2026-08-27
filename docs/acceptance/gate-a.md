# Gate A Acceptance Record

- **Status:** Accepted
- **Acceptance date:** 2026-08-28
- **Repository:** [xiaocaiisxiaocai/dotnet-evm-payment-sandbox](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox)
- **Accepted commit:** [`cb5b5f6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/cb5b5f617828d14ea167fe0be4162f7d8f8f583e)
- **Accepted CI run:** [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588)

> [!WARNING]
> Gate A acceptance applies only to this repository's test-only foundation. It is not a security audit, mainnet approval, custody approval, or authorization to use real funds or customer keys.

## Decision

Gate A is complete. The accepted commit satisfies the bounded foundation criteria through both remote CI and an isolated Windows fresh-clone run. Week 2 is the next learning step; accepting the gate early does not skip the planned study of EVM transactions, receipts, logs, gas, deployment, and revert behavior.

## Evidence summary

| Criterion                      | Accepted evidence                                                                                                                                                                                    |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Reproducible source checkout   | Recursive Windows clone completed in 80.59 seconds, including pinned Git submodules.                                                                                                                 |
| Pinned Foundry installation    | The repository installer downloaded the official Foundry `v1.7.1` asset, verified its fixed SHA-256, and installed it under `.tools/foundry/v1.7.1` in 146.03 seconds.                               |
| Reproducible .NET dependencies | An isolated cold restore with a separate `NUGET_PACKAGES` directory completed in locked mode in 34.89 seconds.                                                                                       |
| Clean full verification        | `scripts/verify.ps1` completed the locked restore, Release build, 15 .NET tests, Foundry format/build/31 tests, dynamic Gitleaks canary, working-tree scan, and full-history scan in 156.68 seconds. |
| Remote CI                      | Run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588) passed all three independent jobs: .NET, Foundry, and secret scanning.                  |
| Repository history             | Gitleaks found no leak in the complete two-commit history after the dynamic canary proved the scanner could detect an EVM-style private key.                                                         |
| Real local deployment          | `DeployLocal` broadcast `PaymentRouter`, `TestUSDC`, and `TestToken18` successfully to a fresh Anvil chain with chain ID `31337`.                                                                    |

The four measured Windows stages totalled 418.19 seconds, or 6 minutes 58.19 seconds. These durations document one cold acceptance run; they are not performance budgets or service-level objectives.

The contract evidence consists of 5 suites, 31 passed, 0 failed, and 0 skipped tests. Fuzz properties ran 256 inputs each; the two invariant campaigns each ran 64 runs by 2,048 calls with zero reverts. Foundry built a 1,030-byte deployed `PaymentRouter` runtime.

## First CI failure and correction

The baseline commit [`c20ee93`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/c20ee93) did not pass its first remote CI run. The .NET job supplied a legacy console logger argument that Microsoft Testing Platform did not accept. The tests themselves were not treated as accepted while the CI invocation was incompatible.

Commit [`cb5b5f6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/cb5b5f617828d14ea167fe0be4162f7d8f8f583e) removed the incompatible logger argument. Follow-up run [`33095409588`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33095409588) then passed all three jobs. Keeping this failed-first-run history is part of the evidence: the gate required the checked-in CI path to work, not only the developer's local command.

## Reproduction on Windows

Prerequisites are Git with submodule support, .NET SDK `10.0.400`, internet access, and Windows PowerShell 5.1 or PowerShell 7.

```powershell
git clone --recurse-submodules https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox.git
Set-Location .\dotnet-evm-payment-sandbox

dotnet --version
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-foundry.ps1 -AddToPath
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

The expected SDK output is `10.0.400`. Under PowerShell 7, use `pwsh -File ./scripts/install-foundry.ps1 -AddToPath` and `pwsh -File ./scripts/verify.ps1`. A run with `-SkipSecretScan` is intentionally degraded and does not reproduce the accepted evidence.

The Anvil broadcast is a separate local check. `DeployLocal` fails closed unless `block.chainid == 31337`; it produces test-only local addresses and no public-network deployment.

## Accepted boundary

Gate A establishes a reproducible repository, exact domain value types, a narrow test-only Router, executable failure/fuzz/invariant evidence, local deployment, and CI/security guardrails. It still does not provide:

- a .NET RPC or generated-contract adapter;
- a Payment Intent API, database, indexer, ledger, finality, or reconciliation;
- production token acceptance policy or unusual-token accounting;
- production signing, custody, key management, monitoring, or incident response;
- a public-network or audited deployment; or
- permission to use mainnet, real funds, or customer keys.

Any future milestone must preserve the accepted invariants and add its own evidence. Gate A acceptance must never be presented as production readiness.
