# Week 3: PaymentRouter v1 behavior and evidence

Week 3 does not introduce a second Router. Gate A already established the
test-only `PaymentRouter`, `TestUSDC`, event, permit path, and broad failure
suite. This stage instead makes the ordinary `pay` path easier to reason about
and turns one previously documented token-risk caveat into executable evidence.

The scope remains deliberately narrow. There is still no API, indexer,
allowlist, production token policy, custody workflow, or settlement decision.
ABI freezing, release hardening, and clean-directory deployment reproducibility
belong to Week 4.

## Read the call in execution order

For an allowance-based payment, the payer first changes state in the token:

```text
payer -> token.approve(Router, amount)
payer -> Router.pay(paymentId, token, merchant, amount)
             -> validate Router inputs
             -> token.transferFrom(payer, merchant, amount)
             -> emit PaymentRecorded(...)
```

`msg.sender` in `PaymentRouter.pay` is the payer. The Router is the spender at
the token boundary because it calls `transferFrom`. The merchant is the direct
token recipient; the intended path does not transfer through the Router's
balance.

Validation precedes the external token call. A zero payment ID, zero or
non-contract token, zero or Router merchant, and zero amount therefore fail
before the Router asks a token to move funds. The event is emitted after the
token call reports success. If any later operation reverts, EVM atomicity rolls
back token state and the event journal for the entire transaction.

## Amounts are token-specific atomic units

The Router neither reads `decimals()` nor converts an amount. `1_000_000` means
one whole `TestUSDC` only because that test token declares six decimals. One
whole `TestToken18` is `1_000_000_000_000_000_000`. In both cases the Router
receives and forwards the raw integer unchanged.

This is why a future caller must bind an amount to a reviewed token address and
known metadata before formatting it for a person. A bare integer cannot say
which asset or display precision it belongs to.

## Allowance belongs to the token

Allowance is not stored in `PaymentRouter`. It is token state keyed by owner and
spender:

```text
allowance[payer][Router] = approved raw amount
```

The ordinary success test grants an exact allowance and proves it becomes zero.
The partial-payment test grants the total once, then proves the first transfer
leaves exactly the second part and the second transfer consumes the remainder.
The insufficient-balance test proves that a reverted transfer leaves allowance
and every relevant token balance unchanged.

An unlimited allowance is operationally different: it normally remains at
`uint256.max`. This repository does not recommend unlimited approval; its main
tests use exact amounts so the authorization boundary is visible.

## What PaymentRecorded means

`PaymentRecorded` states that this Router call completed after the selected
token's `transferFrom` reported success. Its fields are:

| Field       | Meaning                                                       |
| ----------- | ------------------------------------------------------------- |
| `paymentId` | Public correlation value for an off-chain payment intent.     |
| `payer`     | Caller whose token balance and allowance were requested.      |
| `token`     | Contract asked to perform the transfer.                       |
| `merchant`  | Direct recipient passed to the token.                         |
| `amount`    | Gross raw amount requested from the token, not a display value. |

The Router intentionally does not store used IDs. Multiple events with the same
ID may be partial payments, supplements, excess payments, or accidental
duplicates. An indexer must preserve all of them; reconciliation decides how
they relate to the intended amount.

The event is evidence of Router execution, not proof of economic settlement,
token authenticity, finality, or an exact merchant balance delta.

## SafeERC20's exact boundary

`SafeERC20` handles two common interface variants: a standard token returning
`true`, and a legacy token returning no value. It converts an explicit `false`
return into a revert. These behaviors are covered by the normal, no-return, and
false-return fixtures.

It cannot make arbitrary token semantics trustworthy. The Week 3
`FeeOnTransferToken` fixture returns `true` while burning one percent. The Router
therefore emits the requested gross amount even though the merchant receives a
smaller net amount. The test proves all of these facts at once:

- the payer loses the full gross amount;
- the exact allowance is consumed;
- the merchant receives only the net amount;
- the Router retains zero; and
- `PaymentRecorded.amount` remains the requested gross amount.

This fixture documents an unsupported behavior; it does not add
fee-on-transfer support. A production design needs an accepted-token policy and
token-specific reconciliation. Transfer logs are useful evidence, but a
malicious token can also emit dishonest logs, so an allowlist and balance/state
policy cannot be replaced by event parsing alone.

## Evidence matrix

| Requirement                              | Executable evidence                                                       |
| ---------------------------------------- | ------------------------------------------------------------------------- |
| Six-decimal exact transfer               | `test_payTransfersAtomicUnitsAndEmitsCompleteEvent`                       |
| Six- and eighteen-decimal raw amounts    | `test_paySupportsSixAndEighteenDecimalTokensWithoutConversion`            |
| Exact allowance consumption              | Success and partial-payment allowance assertions                          |
| No allowance / insufficient balance      | Explicit ERC-20 error and rollback tests                                   |
| Zero ID, token, merchant, or amount       | Router custom-error tests                                                  |
| Partial and exact repeated IDs            | Two independent same-ID test cases                                         |
| Failed call leaves no logs                | `test_failedPaymentLeavesNoRecordedLogs`                                  |
| Standard, false-return, no-return tokens  | Dedicated success and failure fixtures                                     |
| Fee-on-transfer semantic mismatch         | `test_payRecordsRequestedAmountButCannotValidateFeeOnTransferDelivery`    |
| Random raw amounts and maximum `uint256`  | `PaymentRouterFuzzTest`                                                     |
| Intended path retains no Router balance   | Example assertions and `PaymentRouterInvariantTest`                        |

## A precise non-custodial claim

The intended payment functions ask the token to transfer directly from payer
to merchant and the tests prove zero Router balance for the handled paths. That
does not make the Router address incapable of holding tokens. Anyone can call a
token contract directly and send assets to the Router address. Because this
teaching contract has no rescue function, those unsolicited assets can become
permanently stuck.

The accurate claim is therefore: the tested payment path does not custody
funds. It is not: the Router can never have a token balance.

## Recommended reading order

1. Read `PaymentRouter.pay`, `_validatePayment`, and `_transferAndRecord` in call
   order.
2. Read `TestUSDC` and compare its six decimals with `TestToken18`.
3. Read the success, partial, insufficient-balance, and repeated-ID tests in
   `PaymentRouter.t.sol`.
4. Compare `FalseReturnToken`, `NoReturnToken`, and `FeeOnTransferToken` to see
   exactly what `SafeERC20` can and cannot normalize.
5. Read the fuzz and invariant suites as broader evidence, not as substitutes
   for the concrete semantic examples.
6. Revisit the Week 2 receipt guide and distinguish Router events, token events,
   and post-transaction balance queries.

At the end of Week 3, the useful skill is not memorizing the function signature.
It is being able to state which contract owns each piece of state, which step
can fail, what an event proves, and where reconciliation must remain skeptical.
