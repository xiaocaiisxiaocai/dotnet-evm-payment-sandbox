# Week 18: canonical ERC-2612 permit construction

Week 18 adds `PaymentSandbox.Permits`, an independent class library that builds
one strict ERC-2612 EIP-712 message, verifies the externally supplied EOA
signature, and prepares unsigned `PaymentRouter.payWithPermit` calldata.

This is deliberately separate from SIWE. SIWE proves a login statement for a
relying party; ERC-2612 authorizes a token allowance for one owner, spender,
value, nonce, and deadline. Neither signature inherits the other one's replay
controls or meaning.

## The exact signed facts

The reviewed EIP-712 domain is:

```text
EIP712Domain(
  name,
  version,
  chainId,
  verifyingContract = token
)
```

The signed ERC-2612 message is:

```text
Permit(
  owner,
  spender = reviewed PaymentRouter,
  value = exact raw token amount,
  nonce = explicit observed token nonce,
  deadline
)
```

`PaymentId` and merchant are intentionally absent. Standard ERC-2612 signs an
allowance, not a purchase. The later Router call binds the same permit value to
the payment amount, but the token signature alone does not say which merchant
will receive that transfer or which correlation ID will be emitted.

## Construction boundary

`Erc2612PermitPolicy` accepts only local Anvil (`31337`) or Sepolia
(`11155111`), non-zero and different token/Router addresses, bounded printable
token name/version values, and a whole-second lifetime from one minute through
one hour. Its SHA-256 fingerprint records the complete reviewed policy used to
create a draft; it is provenance and an in-process mismatch guard, not a chain
attestation.

`Erc2612PermitService.CreateDraft` takes an owner, exact `RawTokenAmount`, and an
explicit nonce snapshot. It computes:

```text
domainSeparator = keccak256(abi.encode(domain type hash, ...domain fields))
structHash      = keccak256(abi.encode(Permit type hash, ...permit fields))
digest          = keccak256(0x1901 || domainSeparator || structHash)
```

Addresses and `uint256` values are encoded as 32-byte ABI words. The wallet JSON
uses decimal strings for `uint256` values so JavaScript number precision cannot
silently change a raw amount, nonce, chain ID, or deadline. Time is truncated to
whole seconds, and the deadline is an exclusive upper bound.

The service does not fetch `name()`, `version()`, `DOMAIN_SEPARATOR()`, or
`nonces(owner)` from RPC. In Week 18 those are reviewed caller inputs. Week 19
must add an explicit chain preflight before this can claim that the draft
matches current token state.

## External signing and verification

No private key, account, wallet connector, or signing method exists in the
Permits project. The caller shows `TypedDataJson` to an external wallet and
returns its signature.

Verification first rechecks policy provenance and expiry, then accepts only one
canonical 65-byte EOA signature shape:

- hexadecimal `r || s || v` with a `0x` prefix;
- non-zero `r` and `s`;
- `v` equal to 27 or 28; and
- low-`s` according to secp256k1.

Nethereum performs EIP-712 V4 recovery, and the recovered address must equal the
named owner. Boundary exceptions do not retain library diagnostics, supplied
signature text, or typed-data fragments. `VerifiedErc2612Permit` returns
defensive copies of `r` and `s`; its string form redacts them.

The tests independently compare the service's manually derived
`0x1901 || domainSeparator || structHash` bytes and final digest with
Nethereum's EIP-712 encoder. This catches field order, type, padding, and domain
mistakes instead of proving one implementation only against itself.

## Preparing the Router call

`PreparePayment` requires an already `VerifiedPaymentRouterClient`. It rechecks
that the verified Router chain and address equal the permit policy before using
the existing ABI encoder.

The result contains `RequiredSender = owner`. This is not decorative metadata:
the current Solidity Router passes `msg.sender` to the token's `permit` and then
calls `transferFrom(msg.sender, merchant, amount)`. A relayer submitting the
same calldata would become the owner and fail. The prepared object therefore
does not claim relayer support, signing, broadcasting, receipt success, token
delivery, finality, or settlement.

The raw calldata necessarily contains the wallet signature because the Router
must receive it. `PreparedErc2612Payment.ToString()` redacts the whole calldata;
callers must apply the same rule to logs, exceptions, persistence, and telemetry.

## Replay and front-running boundary

An ERC-2612 token nonce makes one accepted permit unusable again at the token.
Week 18 does not observe that nonce, reserve it, persist draft state, or recheck
it immediately before submission. Consequently:

- a stale caller-supplied nonce can produce a correctly formed but reverting
  transaction;
- another accepted permit can consume the nonce before this call is sent;
- copying the signature can consume the allowance first, causing denial of
  service even though the copied permit cannot redirect the Router payment; and
- process restart loses every off-chain draft/verification fact.

These are the Week 19 state/preflight problems. They must not be hidden behind
the word "verified": Week 18 verifies signature meaning against one immutable
draft, not present chain usability or one-time off-chain orchestration.

## Executable evidence

The focused tests cover:

- manual/Nethereum EIP-712 byte and digest agreement;
- wallet JSON field types and exclusion of merchant/`PaymentId`;
- real generated-key signing and recovered-owner equality;
- value, nonce, chain, token, token name, and spender separation;
- wrong signer, high-`s`, zero value/owner, and out-of-range nonce rejection;
- exact deadline expiry and reviewed lifetime bounds;
- verified Router chain/address binding;
- exact `payWithPermit` ABI decoding and required sender; and
- defensive signature copies plus redacted string output.

The 2026-08-30 clean committed snapshot at implementation commit
[`0b19043`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/commit/0b19043)
passed 312/312 .NET tests, including 10/10 focused Permit tests, 58/58
Authentication tests, and 45/45 API tests. It also passed all 36 unchanged
Foundry tests, the reviewed 1,030-byte/zero-slot Router baseline, and isolated
deployment plus signed Anvil lifecycle replay from 1,274 tracked files. The
dynamic canary and working-tree/complete 44-commit history scans found no leaks.
Remote CI evidence is recorded by GitHub Actions run
[`33309330651`](https://github.com/xiaocaiisxiaocai/dotnet-evm-payment-sandbox/actions/runs/33309330651),
which independently passed locked .NET, Foundry plus signed RPC replay, and
secret-scan jobs.

## Residual limitations

Week 18 deliberately has no:

- RPC observation of token code, name, version, domain separator, or owner nonce;
- durable draft, nonce reservation, submission state, or replay coordination;
- wallet UI, key import, signer, relayer, transaction broadcast, or HTTP endpoint;
- ERC-1271 contract-wallet path or alternate permit dialect such as DAI/Permit2;
- token allowlist, production deployment registry, fee-on-transfer/rebasing
  support, or Sepolia end-to-end evidence; or
- proof of receipt success, merchant balance delta, finality, accounting credit,
  authorization, or settlement.

The existing lower-level Contracts encoder remains intentionally raw for ABI
compatibility. Code that needs the Week 18 checks must compose through the
Permits service rather than treating calldata encoding as approval.

## Suggested reading order

1. `Erc2612PermitPolicy.cs` for the reviewed input envelope.
2. `Erc2612PermitService.CreateDraft` for exact typed-data construction.
3. `Erc2612PermitService.Verify` for canonical EOA recovery and redaction.
4. `Erc2612PermitService.PreparePayment` for Router identity/sender binding.
5. `Erc2612PermitServiceTests.cs` for independent hashes and failure cases.
6. `PaymentRouter.sol` for why `msg.sender` prevents relaying.
