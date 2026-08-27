# Week 2: Observing an EVM Transaction

This exercise connects one successful ERC-20 payment and one intentionally reverted payment to their raw transactions, receipts, logs, gas charges, nonces, and balance effects.

> [!WARNING]
> Run this exercise only against the Anvil process started by the script. It is fixed to chain ID `31337`, uses unrestricted local test tokens and unlocked Anvil accounts, and provides no production signing or public-network deployment path.

## Run the observation

PowerShell 7 or newer is required. Install the pinned repository-local Foundry tools, then run the observer from the repository root:

```powershell
pwsh -File ./scripts/install-foundry.ps1 -AddToPath
pwsh -File ./scripts/observe-week2-transaction.ps1 -Port 18545
```

`-Port` accepts `1` through `65535` and defaults to `8545`. A separate port such as `18545` reduces conflicts with another local node; the script refuses to reuse an occupied port. During cleanup, it requests termination of the owned Anvil process tree and fails the run if the Anvil process does not stop within the bounded wait, including after an assertion failure.

No private key is read, passed as an argument, placed in an environment variable, or printed. The script obtains `accounts[0]` and `accounts[1]` from its own Anvil instance. Forge broadcasts `DeployLocal` with `--unlocked --sender accounts[0]`; later calls use JSON-RPC `eth_sendTransaction` from the same unlocked local account. This convenience is valid only inside the disposable local process.

## Executed path

The observer performs these steps:

1. Start Anvil and require `eth_chainId == 31337`.
2. Use `accounts[0]` as deployer/payer and `accounts[1]` as merchant.
3. Broadcast `DeployLocal`, then obtain the `PaymentRouter`, `TestUSDC`, and `TestToken18` addresses from Forge's broadcast artifact.
4. Mint test USDC to the payer and approve the Router.
5. Call `PaymentRouter.pay` for `1_250_000` raw units, then fetch both the transaction and receipt.
6. Decode `PaymentRecorded`, calculate gas cost, and compare payer, merchant, and Router token balances.
7. Broadcast another `pay` call with `amount = 0` and an explicit gas limit so it is mined and reverts during execution.
8. Fetch the failed receipt and prove that it consumed gas and a nonce while retaining no logs or token balance changes.

The success path observes a real state transition. The failure path observes a real `status = 0` receipt. Neither is an `eth_call` simulation.

## Transaction and receipt are different records

| Question                | Transaction                                                    | Receipt                                                          |
| ----------------------- | -------------------------------------------------------------- | ---------------------------------------------------------------- |
| What is it?             | The sender's request to execute EVM code.                      | The block execution result for that request.                     |
| When can it exist?      | Before inclusion, while pending, or after inclusion.           | Only after the transaction is included in a block.               |
| Important fields        | hash, from, to, nonce, input, value, gas limit, fee parameters | block hash/number, status, gas used, effective gas price, logs   |
| What does it prove?     | The exact submitted request has this identity.                 | That a particular block executed the request with this result.   |
| What does it not prove? | Success, inclusion, finality, or business settlement.          | Finality, token honesty, reconciliation, or business settlement. |

The transaction hash identifies the encoded transaction. It does not say whether the transaction is still pending, was replaced, was dropped, succeeded, or reverted. Receipt `status` supplies the top-level EVM result after inclusion:

- `1` means execution completed without a top-level revert;
- `0` means execution reverted; and
- neither value says that an unusual token delivered the economically expected amount.

## Nonce and calldata

The transaction nonce is the sender account's sequence number. It orders transactions from that account and prevents the same signed transaction from being accepted twice on the same chain. It is not a `PaymentId`, an ERC-2612 permit nonce, or an application idempotency key.

An included successful transaction consumes its nonce. An included reverted transaction also consumes its nonce. By contrast, a request rejected before broadcast has not become a transaction and has no receipt to consume a nonce.

For a contract call, `input` is ABI-encoded calldata:

```text
0x76bbf425 | paymentId | token | merchant | amount
^ selector    four 32-byte ABI words
```

The first four bytes are the function selector:

```text
first4(keccak256("pay(bytes32,address,address,uint256)")) = 0x76bbf425
```

The remaining values are 32-byte ABI words. This call therefore has 132 bytes of calldata: 4 selector bytes plus 4 times 32 argument bytes. A selector identifies a function signature, not a trusted destination; code and chain identity still need separate validation.

## Gas limit, gas used, price, and cost

The transaction's gas field is a limit: the maximum gas units execution may consume before an out-of-gas failure. The receipt's `gasUsed` is the amount actually consumed. They are not interchangeable, and neither is denominated in wei.

The receipt's `effectiveGasPrice` is the actual wei price paid per unit after the transaction's fee caps and the block's fee rules are applied. For these ordinary local transactions, the execution fee is:

```text
gasCostWei = gasUsed * effectiveGasPrice
```

This fee is separate from the ERC-20 amount. A token payment can transfer `1_250_000` raw token units while its gas is charged in the chain's native currency. The summary prints transaction `gasLimit` separately from receipt `gasUsed`, `effectiveGasPriceWei`, and `gasCostWei`. The failed call uses an explicit limit of `300_000` gas. These values must not be collapsed into one ambiguous "gas" number.

A revert does not make execution free. Nodes performed work before reaching the revert, so the failed receipt has `gasUsed > 0` and the sender pays for it. The account nonce and gas charge are protocol effects of the included transaction; they are not rolled back with contract state.

## Reading receipt logs

A receipt log has three parts:

- `address`: the contract that emitted the log;
- `topics`: fixed 32-byte searchable values; and
- `data`: ABI-encoded non-indexed event parameters.

For a non-anonymous event, `topics[0]` is the Keccak-256 hash of its canonical event signature:

```text
keccak256("PaymentRecorded(bytes32,address,address,address,uint256)")
= 0xa3c98d2a8a41cf6c27fd990afbd1c1b88bae461cdd447ca141d7934084b1cc04
```

`PaymentRecorded` has the following layout:

| Location      | Meaning                                     |
| ------------- | ------------------------------------------- |
| log `address` | `PaymentRouter` address                     |
| `topics[0]`   | event signature hash above                  |
| `topics[1]`   | indexed `paymentId`                         |
| `topics[2]`   | indexed `payer`, left-padded to 32 bytes    |
| `topics[3]`   | indexed `merchant`, left-padded to 32 bytes |
| `data` word 0 | non-indexed `token` address                 |
| `data` word 1 | non-indexed `amount` (`1_250_000`)          |

The successful payment receipt also contains the token's `Transfer` log. Its emitter is `TestUSDC`, not the Router. Therefore an observer should identify an event by both emitting address and `topic0`, not by assuming a fixed log-array position.

Indexed parameters are placed in topics in their indexed declaration order. Static values such as this `bytes32` and these addresses can be read from the topic. An indexed dynamic value would instead place a hash in the topic, so the original dynamic value could not be recovered from that topic alone.

## What a real revert demonstrates

The second payment deliberately uses `amount = 0`, which causes `PaymentRouter.InvalidAmount`. The script supplies an explicit `300_000` gas limit. Without it, the helper first calls `eth_estimateGas`; the estimator detects the revert, so the helper never calls `eth_sendTransaction`. That would produce an error but no transaction hash, mined receipt, consumed nonce, or paid gas to observe.

Once the explicit-gas transaction is included, the machine checks require:

- receipt `status == 0`;
- `gasUsed > 0`;
- `gasUsed < gasLimit`, ruling out an out-of-gas failure;
- Anvil's transaction trace returns the `InvalidAmount()` selector;
- the payer's transaction nonce advances by one;
- the receipt retains no logs; and
- payer, merchant, and Router token balances remain unchanged.

Revert rolls back EVM state changes and the transaction's log journal. It does not roll back the included transaction's nonce or gas charge. This particular zero-amount call fails during Router validation, before a token call or event emission, so the observer proves the failed receipt and its no-effect postconditions; it does not by itself prove that an intermediate write was undone. `PaymentRouterPermitTest.test_payWithPermitRollsBackNonceAndAllowanceWhenTransferFails` supplies that separate atomic-rollback evidence.

## Machine-checked expectations

The script exits non-zero on any failed assertion. Its output sections are `Environment`, `Accounts`, `Contracts (parsed from broadcast JSON)`, `Setup transaction hashes`, `Successful payment transaction`, `Successful payment receipt`, `Decoded PaymentRecorded log`, `Successful payment balance checks`, and `Intentionally reverted transaction`, followed by `Assertions` and `ElapsedSeconds`.

Those sections are evidence for the following relationships:

- **Environment:** repository-local Forge, Cast, and Anvil report `v1.7.1`, and the managed node reports chain ID `31337`.
- **Accounts:** payer and merchant come from distinct unlocked accounts owned by this Anvil process; no private key is used.
- **Contracts:** deployment succeeds and yields the Router and two test-token addresses.
- **Successful transaction:** from is the payer, to is the Router, selector is `0x76bbf425`, calldata length is 132 bytes, and its nonce and positive gas limit are decoded.
- **Successful receipt:** status is `1`, transaction and receipt block hashes agree, `0 < gasUsed <= gasLimit`, `effectiveGasPriceWei` is present, and `gasCostWei` is calculated from those receipt values.
- **Payment event:** emitter, `topic0`, payment ID, payer, token, merchant, and raw amount decode to the submitted values.
- **Successful balances:** merchant delta is exactly `1_250_000`, and the Router's token balance is zero.
- **Reverted transaction:** from/to/calldata match the submitted call, gas limit is `300_000`, status is `0`, transaction and receipt block hashes agree, `0 < gasUsed < gasLimit`, the trace returns the `InvalidAmount()` selector, gas cost is positive, the nonce is consumed, no logs remain, and all three token balances are unchanged.
- **Lifecycle:** the observer reports elapsed time and confirms that the Anvil process it started has stopped.

Transaction hashes, contract addresses, block numbers, gas usage, gas prices, and elapsed time can vary between runs. The script asserts their relationships and types, not one captured set of incidental values.

## A receipt is not finality

A successful receipt proves inclusion and execution in one particular block. On a public chain, that block can still be replaced by a reorganization. A production indexer must track the receipt's block hash against the canonical chain, wait for a chain-specific confirmation or finality policy, and reverse prior observations when necessary.

Anvil normally mines immediately and is intentionally convenient for this exercise. Its fast receipt does not model public RPC uncertainty, propagation, replacement transactions, reorgs, or economic finality. Gate A and Week 2 evidence must not be described as production settlement.

## Reading order

1. Read `contracts/src/PaymentRouter.sol` to identify validation, transfer, and event order.
2. Read `contracts/src/testtokens/TestUSDC.sol` to see why `1_250_000` is an exact six-decimal raw amount.
3. Read `contracts/script/DeployLocal.s.sol` to find the `31337` fail-closed boundary.
4. Read `scripts/observe-week2-transaction.ps1` from parameter validation and RPC helpers through process cleanup.
5. Follow the successful mint/approve/pay path, mapping each printed field to the transaction, receipt, event, or balance query that produced it.
6. Follow the explicit-gas zero-amount path and identify which effects are retained (gas and nonce) and which application effects are absent (token balance changes and logs).
7. Compare the live observations with `contracts/test/PaymentRouter.t.sol`; tests state invariants, while the observer exposes the RPC evidence behind them.

After reading, be able to explain every printed field without using "transaction," "receipt," "event," and "payment" as synonyms.
