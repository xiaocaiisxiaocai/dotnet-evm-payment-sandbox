# Week 14: ephemeral Anvil signing and real RPC lifecycle

Week 14 closes the deliberate trust gap left by Week 13. The transaction
lifecycle now has one concrete signer and one concrete RPC adapter, but both are
strictly limited to a disposable loopback Anvil chain. The implementation does
not import a key, read an environment secret, connect to Sepolia, or create a
hosted wallet.

The important result is not merely that `eth_sendRawTransaction` succeeds. The
verification proves that the exact policy-approved EIP-1559 fields were signed,
that an ambiguous accepted broadcast reuses the same bytes, and that a
higher-fee replacement keeps the same payer nonce and payment meaning.

## Safety boundary

`EphemeralAnvilWallet` generates a fresh 32-byte secp256k1 key with the operating
system CSPRNG. Its public API exposes only the derived address and a signer bound
to one `TransactionLifecyclePolicy`.

The wallet:

- binds only to chain ID `31337` and its own signer address;
- requires the policy Router, positive bounded gas and fees, zero native value,
  the exact `pay` selector, and the fixed four-word calldata length;
- never returns, imports, serializes, logs, or stores the private key;
- signs while holding a short process-local lock; and
- performs a best-effort zero of its owned key byte array on disposal.

Managed runtimes and cryptographic libraries may create internal copies that
cannot be proven erased. This is therefore a test fixture, not custody or a
production key-management design.

## Signed transaction round-trip

An unsigned fingerprint proves what the Orchestrator asked a signer to sign; it
does not prove what opaque bytes contain. `SignedEip1559TransactionVerifier`
adds that missing cryptographic boundary.

For every signed attempt it:

1. decodes the opaque bytes through Nethereum;
2. requires typed EIP-1559 transaction type `0x02`;
3. re-encodes the decoded transaction and compares the complete canonical
   bytes, preventing acceptance of a valid prefix plus ignored trailing data;
4. recomputes and compares the transaction hash;
5. compares chain ID, nonce, both fee fields, gas limit, Router destination,
   zero value, complete calldata, and an empty access list; and
6. recovers the secp256k1 signer and compares its address with the approved
   signer.

Any mismatch fails before the payload leaves the signer. Decoder and signature
exceptions are reduced to a bounded exception type; raw signed bytes, library
messages, and inner exceptions are not echoed.

## Loopback-only RPC adapter

`LocalAnvilRpcClient` implements the narrow identity, pending-nonce, raw
broadcast, and receipt interfaces already owned by the Orchestrator and
Contracts layers. Its options accept only an absolute, credential-free
`http://` loopback URL with a 1-30 second request timeout.

Connection requires `web3_clientVersion` beginning with `anvil/` and
`eth_chainId == 31337`. The adapter rechecks chain ID before every read or side
effect. It reads the `pending` nonce, verifies that Anvil returns the same hash
for submitted bytes, requires complete receipt fields, and maps only bounded
broadcast outcomes.

Anvil's `already known`, `known transaction`, and `already imported` variants
all mean that the exact transaction is already present. `nonce too low` stays
ambiguous because only receipt observation can decide whether these exact bytes
were mined. Transport exceptions become the processor's durable
`unknown/transport_error` observation without retaining endpoint or raw-payload
details.

## Real unknown-result and replacement scenario

`PaymentSandbox.Orchestrator.Anvil` is a console verification harness, not a
shipping application. The clean-source observer deploys the reviewed Router and
test token to a script-owned Anvil process, then supplies only their public
addresses and reviewed runtime hash.

The harness performs this sequence:

1. generate an ephemeral payer and verify the Router identity;
2. fund the payer with local ETH, mint exactly 1,250,000 token base units, and
   use Anvil impersonation only to establish the Router allowance;
3. disable automining so competing same-nonce attempts remain pending;
4. reserve a nonce, sign, round-trip verify, and durably store the initial
   transaction;
5. submit it to Anvil, then deliberately throw away the accepted response;
6. persist `BroadcastUnknown`, reload the same raw bytes, and submit them again;
7. create a fee-only replacement with the same nonce and calldata, but a new
   transaction hash;
8. mine one block and observe that the higher-fee replacement succeeded; and
9. verify exactly two attempts, equal nonces, different hashes, an exact
   merchant balance increase, an empty payer token balance, and zero Router
   token custody.

The harness prints public addresses, transaction hashes, state, and balance
delta. It never prints the private key or raw signed transactions. A uniquely
named temporary lifecycle database is removed after SQLite pools are cleared,
and automining is restored even after failure.

## Why Anvil impersonation is not the signer

The setup phase impersonates the freshly generated payer only for ERC-20
`approve`, because the test token must grant allowance before `pay`. The actual
payment attempts are independently signed with the ephemeral private key and
broadcast through `eth_sendRawTransaction`. Treating an unlocked or impersonated
account as proof of signing would not test transaction encoding, recovery, or
unknown raw-transaction replay.

## Residual limitations

This milestone still does not provide:

- a production, imported-key, hardware-wallet, KMS, or Sepolia signer;
- secure key backup, access control, rotation, attestation, or memory-locking;
- a credential-bearing or non-loopback RPC client;
- a hosted lifecycle worker, API endpoint, scheduler, or operator approval;
- cross-host nonce coordination or encrypted/tamper-evident lifecycle storage;
- token-delivery proof, protocol finality, accounting credit, or settlement.

The general lifecycle policy still models Sepolia, but the concrete Week 14
wallet and RPC adapter reject it. Expanding that boundary requires a separate
key-provider design and threat-model review; changing one chain constant is not
sufficient.

## Verification coverage

Network-free tests cover exact EIP-1559 round-trip, changed expected facts,
wrong destination, disposed-key behavior, loopback URL restrictions, and
timeouts. The clean Anvil replay supplies real acceptance, duplicate import,
same-nonce replacement, receipt, and balance evidence on every supported local
and CI verification run.

The 2026-08-30 committed-snapshot verification of implementation commit
`e3ec705` passed 235/235 .NET tests, including 42/42 focused Orchestrator tests.
It also passed all 36 unchanged Foundry tests, the reviewed
1,030-byte/zero-storage-slot Router baseline, and a clean deployment plus Week
14 signed lifecycle from 1,221 Git-known files. The dynamic secret canary,
candidate-tree scan, and complete 32-commit history scan found no leaks.
GitHub Actions run
[`33295738973`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33295738973)
then independently passed all three jobs: locked .NET build/tests, Foundry
build/tests plus real signed RPC replay, and working-rules/full-history secret
scan.

## Suggested reading order

1. `Infrastructure/EphemeralAnvilWallet.cs` for key lifetime and policy binding.
2. `Infrastructure/SignedEip1559TransactionVerifier.cs` for the round-trip proof.
3. `Infrastructure/LocalAnvilRpcClientOptions.cs` for the endpoint boundary.
4. `Infrastructure/LocalAnvilRpcClient.cs` for RPC mapping and ambiguity rules.
5. `tests/PaymentSandbox.Orchestrator.Anvil/Program.cs` for the real scenario.
6. `scripts/observe-week2-transaction.ps1` for disposable Anvil ownership.
7. Infrastructure unit tests for fail-closed examples.

## What should come next

Week 15 should begin the authentication track without coupling it to payment
signing: define a SIWE message/domain/nonce model, keep login signatures
separate from Router payments and permits, and first prove parser and replay
failure cases without adding a public endpoint.
