# Week 5: typed contract access behind an identity gate

Week 5 introduces the first .NET adapter that understands `PaymentRouter`. It
does not introduce an API process, wallet, signer, transaction broadcaster,
receipt poller, indexer, or settlement decision. The deliverable is narrower:
turn the reviewed Week 4 ABI into typed local messages and refuse to expose the
encoder until a read-only RPC endpoint reports the expected chain and deployed
runtime code.

## Responsibility map

| Area | Responsibility |
| --- | --- |
| `ContractDefinition/PaymentRouterDefinition.cs` | Mechanical typed projection of the committed ABI |
| `PaymentRouterTrustPolicy` | Validated operator expectations: chain ID, address, runtime Keccak |
| `IPaymentRouterIdentityRpc` | Only the two required observations: chain ID and code |
| `NethereumPaymentRouterIdentityRpc` | Nethereum implementation using `eth_chainId` and `eth_getCode` at `latest` |
| `PaymentRouterIdentityVerifier` | Ordered, fail-closed comparison of observations with policy |
| `PaymentRouterConnector` | Returns a client only after verification succeeds |
| `VerifiedPaymentRouterClient` | Locally encodes unsigned `pay` and `payWithPermit` calldata |

The dependency direction is:

```text
PaymentSandbox.Domain
          ^
          |
PaymentSandbox.Contracts ----> Nethereum
          ^
          |
future API / application layer
```

Domain still has no Nethereum reference. The adapter converts `PaymentId` to
`bytes32` and `RawTokenAmount` to `uint256`; it does not move RPC concerns into
those value types.

## Why there is no contract query service

`PaymentRouter` has no view function and no storage. Its public ABI contains
two state-changing functions, one event, and five errors. Adding a method such
as `GetPaymentStatusAsync(paymentId)` would invent a capability the contract
does not have and would encourage callers to confuse off-chain interpretation
with on-chain state.

Week 5 therefore uses RPC only for destination identity. Event queries belong
to the later reorg-aware indexer, where block ranges, canonicality, duplicates,
and finality can be handled together.

## Typed ABI projection

The committed ABI maps to these Nethereum types:

| Solidity ABI item | .NET type |
| --- | --- |
| `pay(bytes32,address,address,uint256)` | `PayFunction` |
| `payWithPermit(bytes32,address,address,uint256,uint256,uint8,bytes32,bytes32)` | `PayWithPermitFunction` |
| `PaymentRecorded(...)` | `PaymentRecordedEventDto` |
| Router custom errors | `InvalidAmountError`, `InvalidMerchantError`, `InvalidPaymentIdError`, `InvalidTokenError` |
| OpenZeppelin error visible through Router | `SafeErc20FailedOperationError` |

The selector tests independently retain the reviewed Week 4 values:

- `pay`: `0x76bbf425` and 132 total calldata bytes;
- `payWithPermit`: `0x1f2b568e` and 260 total calldata bytes; and
- `PaymentRecorded`: `paymentId`, `payer`, and `merchant` are indexed while
  `token` and `amount` are data fields.

The typed definition is a consumer of `contracts/abi/PaymentRouter.json`, not a
second source of contract truth. An intentional ABI change must update the
contract baseline, typed projection, selector/event tests, and affected callers
in one review.

## Fail-closed identity sequence

Connection follows one fixed order:

```text
validate local expected chain/address/hash
  -> eth_chainId
  -> stop immediately if chain differs
  -> eth_getCode(configured address, latest)
  -> reject missing or malformed bytes
  -> Keccak-256(exact runtime bytes)
  -> compare with reviewed expected hash
  -> return VerifiedPaymentRouterClient
```

The chain check occurs before `eth_getCode`. A wrong-chain endpoint therefore
cannot persuade this connector to continue by returning interesting code at the
same address. The code check hashes bytes, not a text representation, so hex
letter casing does not affect identity.

The reviewed v1 runtime constant is copied from
`contracts/baselines/PaymentRouter.v1.json`:

```text
0x8308fbd23f6bd4bcb4284281ab9388b2a437297aa512a8308b4c2e390205e92c
```

The policy still requires the caller to provide an expected hash explicitly.
That makes deployment selection visible at the composition boundary and permits
a later reviewed v2 deployment without silently changing v1 behavior.

## What verification does and does not prove

A successful result means one endpoint reported:

1. the expected chain ID; and
2. bytecode at the configured address whose Keccak matches the policy.

It catches many configuration mistakes: wrong endpoint, wrong chain, wrong
address, empty address, EOA address, malformed RPC data, stale deployment, or a
different build. It does not make the endpoint an independent trust root. A
malicious provider can lie consistently about both observations. `latest` can
also reorg, and the result is point-in-time rather than a permanent capability.

Later milestones still need trusted-block anchoring, independent provider or
checkpoint comparison for critical data, reorg handling, finality, and startup
or endpoint-switch lifecycle checks. A verified client must never be treated as
proof that a payment settled.

## Calldata is not authorization

After verification, the client can produce:

```csharp
EncodedPaymentRouterCall call = client.EncodePay(
    paymentId,
    tokenAddress,
    merchantAddress,
    new RawTokenAmount(1_000_000));
```

The result has only `ContractAddress` and `Data`. It has no sender, nonce, gas,
fee, signature, or transaction hash. Encoding a valid shape proves neither
user consent nor business validity.

The adapter mirrors immediate Router preconditions before encoding:

- payment amount must be positive;
- token and merchant must be valid non-zero addresses;
- merchant cannot be the Router;
- permit deadline must fit `uint256`; and
- `r` and `s` must each contain exactly 32 bytes.

It deliberately does not verify token code, accepted-token policy, balances,
allowance, permit nonce/signature, merchant ownership, payment-intent state, or
settlement. Those facts belong to different boundaries.

## RPC and dependency boundary

`IPaymentRouterIdentityRpc` makes network use explicit and keeps all unit tests
offline. Its Nethereum implementation internally owns `IWeb3`, but the adapter's
public contract exposes no broad Web3 object. Nethereum 6.1.0 has no cancellation
parameter on these two requests, so the adapter uses `Task.WaitAsync` to preserve
caller cancellation while the underlying HTTP operation finishes independently.

During the first restore, NuGet selected Nethereum's permitted minimum
`Newtonsoft.Json 11.0.2`, which has a high-severity advisory. Because warnings
are errors, restore failed rather than accepting it. The Contracts project now
directly pins compatible `Newtonsoft.Json 13.0.4`; the vulnerability warning was
not suppressed. Central versions and project lock files make that decision
reviewable and reproducible.

## Failure evidence

The network-free tests cover:

| Failure | Expected behavior |
| --- | --- |
| Invalid local chain, address, or hash | Policy construction fails before RPC exists |
| Wrong observed chain | `UnexpectedChainId`; no code request occurs |
| Empty code / `0x` | `CodeMissing` |
| Non-prefixed, odd-length, or non-hex code | `CodeMalformed` |
| Different runtime digest | `RuntimeCodeHashMismatch` with expected/observed hashes |
| RPC exception | Wrapped as `RpcRequestFailed` with the original inner exception |
| Caller cancellation | `OperationCanceledException` remains cancellation, not identity failure |
| Invalid calldata argument | Local argument exception; no RPC or send operation |

The success test also records call order (`chainId`, then configured-address
code), normalizes `0X`/uppercase reviewed values, and compares a known Keccak
test vector rather than deriving the expected digest through production code.

## Quick start

Run the .NET boundary without an RPC endpoint:

```powershell
dotnet restore .\PaymentSandbox.slnx --locked-mode
dotnet build .\PaymentSandbox.slnx --configuration Release --no-restore
dotnet test .\PaymentSandbox.slnx --configuration Release --no-build --no-restore
```

Run the complete repository evidence, including contract baseline and clean
Anvil replay:

```powershell
pwsh -NoProfile -File .\scripts\verify.ps1
```

For a future local composition root, construction has this shape:

```csharp
var rpc = new NethereumPaymentRouterIdentityRpc("http://127.0.0.1:8545");
var policy = new PaymentRouterTrustPolicy(
    expectedChainId: 31_337,
    contractAddress: configuredRouterAddress,
    expectedRuntimeCodeKeccak256: PaymentRouterArtifact.RuntimeCodeKeccak256);

VerifiedPaymentRouterClient client = await new PaymentRouterConnector(rpc)
    .ConnectAsync(policy, cancellationToken);
```

The example does not broadcast. A local deployment address is intentionally
configuration, not a hard-coded repository constant.

## Remaining boundary

Week 6 may consume this library from a Payment Intent API, but it must not turn
intent creation into signing, broadcasting, indexing, or credit. The first API
should establish HTTP validation and idempotent intent state while continuing to
treat chain observations and payment settlement as later capabilities.

## Verification evidence

The Week 5 implementation is commit
[`9969cd6`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/9969cd6).
On 2026-08-29, the supported Windows verification entry point passed 26
network-free Contracts tests plus 15 Domain tests, for 41 .NET tests with zero
failed or skipped. The unchanged contract boundary also passed all 36 Foundry
tests: four fuzz properties each ran 256 inputs, and four invariant campaigns
each ran 64 runs by 2,048 calls with zero handler reverts.

The same run rechecked the 1,030-byte runtime, zero storage slots, and reviewed
runtime Keccak. It copied 1,033 Git-known files into a disposable source tree,
compiled and deployed there, checked successful and reverted transaction
evidence, stopped Anvil, and confirmed temporary-directory cleanup. The dynamic
Gitleaks canary, candidate working tree, and complete nine-commit history scan
also passed.

GitHub Actions run
[`33257669877`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33257669877)
then passed all three jobs on Ubuntu: locked .NET build/tests, Foundry
build/tests/clean RPC replay, and working-rule plus full-history secret scanning.
This remains bounded test evidence, not an RPC trust guarantee, audit, public
deployment, or production authorization.
