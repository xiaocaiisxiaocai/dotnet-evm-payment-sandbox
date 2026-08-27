# ADR 0001: Scope and Security Boundaries

- **Status:** Accepted
- **Date:** 2026-08-27
- **Decision owners:** Repository maintainers

## Context

The repository is a 24-week learning and portfolio project for a .NET backend developer entering EVM integration work. The useful evidence is not the number of protocols or chains touched; it is whether one bounded system can preserve exact amounts, correlate payments, recover from failures, and explain its security limits.

Starting with a production wallet, multiple chains, or a complete payment platform would combine unfamiliar contract, RPC, accounting, signing, and operational risks before any boundary is testable. It would also make a green demo easy to mistake for production readiness.

## Decision

We will build one test-only EVM payment sandbox under the following boundaries:

1. Use local Anvil for normal development and Ethereum Sepolia only for a bounded smoke test later in the roadmap.
2. Do not use mainnet, real funds, customer keys, custody, or production signing workflows.
3. Keep the primary payment path non-custodial: a payer-authorized token transfer goes directly to the merchant; the Router does not retain balances.
4. Keep Domain independent of Nethereum, ASP.NET, persistence, RPC, and signing SDKs.
5. Treat .NET and Solidity as separate build systems with one fresh-checkout verification contract.
6. Pin tool and direct dependency versions, commit transitive dependency lock files, and reject silent dependency graph changes in CI.
7. Add API, contract adapters, indexing, ledger, orchestration, and authentication only in their scheduled milestone, with failure tests and updated boundary documentation.
8. Treat a successful test as evidence for the tested behavior only. It is not a security audit, custody approval, or production deployment authorization.

The current working tree implements the repository foundation, two domain values, a test-only non-custodial Router, six- and eighteen-decimal test tokens, permit tests, failure tests, fuzz tests, invariant tests, local Anvil deployment, and CI security guardrails. The local contract evidence is 31 passing Foundry tests and a successful real Anvil broadcast. Implementing this evidence ahead of the weekly schedule does not waive acceptance: Gate A remains in progress until fresh-checkout and remote CI criteria pass.

## Consequences

### Benefits

- Current claims stay aligned with executable evidence.
- Domain invariants can be learned and tested without RPC or wallet complexity.
- Later infrastructure can be replaced without contaminating business types.
- Toolchain drift and dependency drift become visible review events.
- No secret is required to build or test the Week 1 repository.

### Costs

- The first milestone has no interactive application or .NET-to-chain integration; the contract can only be exercised through local/test tooling.
- Some directories shown in the long-term plan do not exist until their milestone begins.
- Exact SDK and tool pins require contributors to install the documented versions.
- Mainnet and production operational questions remain intentionally unanswered by this sample.

## Alternatives considered

### Build the full stack immediately

Rejected because failures would cross too many new boundaries at once. It would be difficult to tell whether a defect belongs to the contract, RPC adapter, persistence, accounting, or signer.

### Start with a custodial server wallet

Rejected because key custody and automated value movement have the highest consequence. A learning repository cannot justify that production risk through functional tests alone.

### Support several EVM chains from the first release

Rejected because shared JSON-RPC concepts do not make chains operationally identical. Finality, fee policy, RPC behavior, token addresses, and incident procedures remain chain-specific.

### Use floating-point or display-decimal amounts in Domain

Rejected because EVM token values are unsigned integers and financial rounding must be explicit at an input or presentation boundary.

### Mock every external boundary

Rejected as the only test strategy. Unit tests are appropriate for Domain, but later milestones must exercise real local EVM behavior with Anvil and explicit fault tests.

## Revisit conditions

Revisit this decision before any of the following:

- adding a mainnet configuration;
- accepting or storing a key that controls material value;
- holding funds in a contract or service account;
- presenting the sample as production-ready;
- adding another chain beyond the documented test scope; or
- offering a customer-facing deployment or operational SLA.

Any such change requires a new decision record, updated threat model, explicit ownership, independent security review appropriate to the risk, and production operational controls outside the current Gate A scope.
