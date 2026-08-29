# Week 4: reviewed contract baseline and clean replay

Week 4 turns the already-tested `PaymentRouter` into a deliberately reviewed
v1 contract baseline. It does not add production controls or declare the
contract audited. The goal is narrower: detect an accidental change to the
interface, compiler inputs, storage shape, or runtime bytecode, and prove that
the tracked source can still compile, deploy, and execute outside the normal
workspace's generated artifacts.

## What is frozen

The reviewed inputs are intentionally explicit:

| Input                         | Reviewed value                                            |
| ----------------------------- | --------------------------------------------------------- |
| Foundry release               | `1.7.1`, release commit recorded in the baseline          |
| Solidity compiler             | `0.8.36`                                                  |
| EVM target                    | `prague`                                                  |
| Optimizer                     | enabled, 200 runs                                         |
| Solidity metadata bytecode    | IPFS hash mode                                            |
| OpenZeppelin Contracts        | `5.7.0` at gitlink `cab1993...`                           |
| forge-std                     | `1.16.1` at gitlink `620536f...`                          |
| `PaymentRouter` runtime       | 1,030 bytes and a reviewed Keccak-256                     |
| `PaymentRouter` storage slots | zero                                                      |

The repository-local Foundry installer pins the official release archive by
platform-specific SHA-256. The baseline additionally checks the commit printed
by `forge --version`. The dependency checks compare both package version and
exact submodule commit and reject dirty submodule working trees.

These checks are complementary. A Solidity pragma alone does not pin Foundry,
an npm package version alone does not pin a Git checkout, and a Git commit alone
does not describe optimizer or EVM settings.

## The committed ABI is a consumer contract

`contracts/abi/PaymentRouter.json` is the standard ABI array intended for later
typed client generation. It contains:

- `pay(bytes32,address,address,uint256)`;
- `payWithPermit(bytes32,address,address,uint256,uint256,uint8,bytes32,bytes32)`;
- `PaymentRecorded(bytes32,address,address,address,uint256)`; and
- the Router and linked `SafeERC20` errors that callers may receive.

The companion baseline records the function selectors, error selectors, and
event topic explicitly. `verify-contract-baseline.ps1` asks Forge to regenerate
the ABI from source, parses both files as JSON, and compares their structured
values. Whitespace changes do not matter; a changed name, type, indexed flag,
mutability, selector, or event topic fails verification.

An ABI freeze is not an address guarantee. A future .NET adapter must still
check chain ID, configured address, and deployed code identity before trusting
that an RPC destination implements this interface.

## Runtime bytecode and metadata

The baseline hashes the complete deployed runtime bytecode returned by
`forge inspect`, including Solidity's metadata suffix. This is intentionally
strict. Changing executable logic, compiler settings, dependency source, or
metadata-relevant source can change the hash and require review.

The runtime hash proves reproducibility for these exact source and toolchain
inputs. It does not prove that a contract at some network address has this code;
that requires `eth_getCode` on the intended chain. It also does not prove the
code is secure. A reproducible defect remains a defect.

The committed value is Keccak-256, the same hash family used by EVM interface
identifiers and code-oriented workflows. It must not be confused with the
SHA-256 values used to authenticate downloaded tool archives.

## Why an empty storage layout is checked

`PaymentRouter` v1 is stateless. Forge currently reports:

```json
{
  "storage": [],
  "types": {}
}
```

Checking this shape catches an accidental owner, allowlist, used-payment map,
pause flag, or other persistent state even if a developer overlooks it during
review. Zero storage slots does not mean no state changes occur anywhere: token
balances, allowances, permit nonces, account nonces, and logs belong to other
contracts or protocol state.

This is not an upgradeable proxy storage-compatibility scheme. The Router has no
proxy or upgrade mechanism; the empty layout is a reviewed architectural fact.

## Stronger fuzz properties

The Week 4 fuzz suite adds two properties:

1. Two positive random parts using the same payment ID preserve exact payer,
   merchant, allowance, and zero-Router accounting across the partition.
2. Every fuzzed amount above the payer's balance reverts with the expected ERC-20
   error and restores the exact allowance and all relevant balances.

The existing six- and eighteen-decimal fuzz tests now also assert exact
allowance consumption. The maximum-`uint256` test records the standard
OpenZeppelin behavior that an unlimited allowance remains `uint256.max` after a
transfer.

Fuzzing samples many inputs; it is not exhaustive formal verification. Bounding
is part of each property and must be read as carefully as the assertions.

## Stronger stateful invariants

The handler still exercises only reviewed direct `TestUSDC` payments. Four
invariants now run after random call sequences:

- the Router balance remains zero;
- merchant balance equals cumulative handled payments;
- handler balance equals starting balance minus cumulative payments; and
- handler + merchant + Router balances equal total supply, while total supply
  and unlimited Router allowance remain unchanged.

These relationships detect accounting errors that one final-balance assertion
could miss. They do not cover arbitrary tokens, permit calls, unsolicited
transfers to the Router, reentrancy from malicious tokens, public-chain reorgs,
or application reconciliation.

## Clean tracked-source replay

Normal builds can accidentally depend on `contracts/out`, cache files, previous
broadcast JSON, untracked source, or a modified dependency checkout. The clean
replay creates a temporary source tree from Git-known files only:

```text
current checkout and index
  -> verify direct submodule commits and clean working trees
  -> reject untracked contract or verification source
  -> copy tracked root files
  -> expand the two direct pinned contract dependencies
  -> skip their unrelated optional nested test gitlinks
  -> compile and deploy from the temporary source root
  -> execute successful and reverted payment observations
  -> stop owned Anvil and remove the temporary directory
```

The wrapper reuses the Week 2 observer rather than duplicating RPC and process
lifecycle logic. The observer accepts a source root for Forge compilation while
continuing to use the original repository's checksum-verified Foundry binaries.
Generated output is therefore isolated without copying 183 MB of tools.

New files under contract or verification source paths must be staged before a
clean replay. Staging does not approve the change; it makes the exact candidate
file set visible to Git so the isolated snapshot can include it.

## English quick start

From a clone on Windows, Linux, or macOS with PowerShell 7 and .NET SDK
`10.0.400`:

```powershell
git submodule update --init -- contracts/lib/openzeppelin-contracts contracts/lib/forge-std
pwsh -NoProfile -File ./scripts/install-foundry.ps1 -AddToPath
pwsh -NoProfile -File ./scripts/verify.ps1
```

The final command is the supported one-step check. It runs locked .NET
restore/build/tests, Solidity formatting/build, the reviewed Router baseline,
all Foundry tests, clean tracked-source deployment/transaction replay, a dynamic
secret-scanner canary, the working-tree scan, and the complete Git-history scan.

For focused diagnosis after the tool install:

```powershell
# Recompile and compare ABI, selectors, storage, versions, size, and code hash.
pwsh -NoProfile -File ./scripts/verify-contract-baseline.ps1

# Replay compile/deploy/success/revert from isolated tracked source.
pwsh -NoProfile -File ./scripts/verify-clean-contract-deployment.ps1 -Port 19545
```

Both commands return a non-zero exit code on drift. The clean replay refuses an
occupied port and removes only a validated, randomly named directory beneath
the operating system's temporary directory.

## Updating the baseline intentionally

A failing baseline is a review signal, not a file-generation inconvenience.
When an intentional contract or compiler change arrives:

1. read the source and dependency diff;
2. run the full example, permit, fuzz, invariant, and clean-replay checks;
3. inspect the regenerated ABI, selectors, event topics, storage layout,
   runtime size, and runtime hash;
4. identify every downstream ABI consumer and deployment whose assumptions
   change;
5. update the ABI and baseline in the same reviewed change; and
6. document whether the change is compatible, requires a new deployed address,
   or invalidates earlier evidence.

Never update the expected hash solely to turn CI green. The expected value is
useful only when a human can explain why it changed.

## Remaining boundary

Week 4 establishes reproducible local evidence for a test contract. It still
does not provide a generated .NET binding, RPC trust policy, deployment
registry, public testnet address, audit, production token policy, monitoring,
or incident response. Those remain later milestones rather than implied
features of a green baseline check.
