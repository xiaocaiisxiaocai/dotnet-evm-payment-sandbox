# Week 15: bounded SIWE challenge verification

Week 15 begins the authentication track with a class library, not a login
endpoint. `PaymentSandbox.Authentication` issues short-lived Sign-In with
Ethereum challenges, renders one canonical message, verifies an ERC-191 EOA
signature, and atomically consumes the server nonce exactly once.

The design follows [ERC-4361](https://ercs.ethereum.org/ERCS/erc-4361) and its
[ERC-191](https://ercs.ethereum.org/ERCS/erc-191) signing requirement, but
implements an explicit strict subset rather than claiming to be a general SIWE
parser.

## Authentication is not payment authorization

A valid SIWE signature proves that one address controlled an EOA key when it
signed one relying-party login challenge. It does not authorize:

- a Router payment or ERC-20 allowance;
- an ERC-2612 permit;
- an Orchestrator transaction;
- a merchant, amount, `PaymentId`, refund, payout, or settlement; or
- access to any application resource.

`SiweAuthenticationResult` therefore contains only address, chain, and observed
authentication time. It is not a cookie, bearer token, session, role, tenant, or
authorization decision. Authentication and payment signing remain separate
projects and message domains.

## The supported ERC-4361 subset

The full standard has optional scheme, statement, not-before, request ID, and
resources fields, plus EOA and contract-account verification paths. Week 15
accepts exactly one canonical shape:

```text
<domain> wants you to sign in with your Ethereum account:
<EIP-55 checksum address>

<fixed server statement>

URI: <fixed same-origin HTTPS URI>
Version: 1
Chain ID: <31337 or 11155111>
Nonce: <32 lowercase hexadecimal characters>
Issued At: <UTC RFC 3339 to whole seconds>
Expiration Time: <UTC RFC 3339 to whole seconds>
```

It rejects explicit schemes in the header, HTTP, credentials, query strings,
fragments, cross-origin request URIs, optional SIWE fields, resources, contract
wallet signatures, Unicode statements, CRLF, trailing lines, and noncanonical
numbers or timestamps. A strict conforming subset is easier to reason about
than a permissive parser that silently ignores fields it does not understand.

## Relying-party policy

`SiweAuthenticationPolicy` fixes:

- one canonical HTTPS DNS origin and its authority;
- one same-origin request URI;
- one exact human-readable ASCII statement;
- local Anvil `31337` or Sepolia `11155111`;
- a 1-10 minute whole-second challenge lifetime; and
- at most one minute of issued-at clock skew.

Ethereum mainnet is rejected by an allowlist, not a weak “not mainnet” check.
The complete policy meaning has a deterministic SHA-256 fingerprint. A stored
challenge issued under another policy cannot be consumed merely because some
visible fields still happen to match.

There is no HTTP request-origin object yet. A future endpoint must take its
expected origin from trusted server configuration, not copy an untrusted
`Host`, `Origin`, redirect, or proxy header into the signed message.

## Server nonce and challenge lifetime

`IssueChallengeAsync` obtains 16 bytes from the operating-system CSPRNG and
encodes them as 32 lowercase hexadecimal characters. This is 128 bits of
entropy while remaining inside ERC-4361's alphanumeric nonce grammar.

The challenge is created before any wallet address is trusted. The wallet
address becomes part of the displayed message, and successful signature
recovery binds the consumed nonce to that address. Stealing an unused nonce may
allow denial of service by consuming it for a different address, but it does not
let the attacker authenticate as the victim without the victim's signature. A
real web flow must additionally bind challenge delivery to its browser/session
initiation context.

Issued and expiry times are truncated to whole UTC seconds so storage, rendering,
parsing, and comparison have one canonical representation.

## Canonical parser

`SiweMessageParser` caps input at 4 KiB and then requires exact line count,
labels, blank lines, casing, field order, and newline form. It also requires:

- the authority to round-trip through a canonical HTTPS DNS URI;
- the address text to equal its EIP-55 checksum form;
- statement characters to belong to the supported ERC-4361 ASCII set;
- URI text to equal its canonical absolute form;
- version exactly `1`;
- positive decimal chain ID without leading zeroes;
- an 8-64 character alphanumeric nonce; and
- expiration later than issued-at.

After parsing, it renders the object again and requires byte-for-character
equality with the original .NET string. This prevents an apparently equivalent
but differently interpreted message from reaching signature recovery.

Parser failures collapse to `MalformedMessage`. Framework exception messages
and attacker-controlled input are not retained as inner exceptions.

## ERC-191 EOA recovery

`SiweEoaSignatureVerifier` uses Nethereum's `EthereumMessageSigner`, which
applies the personal-sign prefix required by ERC-191:

```text
\x19Ethereum Signed Message:\n<UTF-8 byte length><message>
```

The bounded verifier requires a 65-byte `r || s || v` signature with `v` equal
to wallet-standard `27` or `28`, rejects zero `r` or `s`, recovers the address,
and compares it with the checksummed address inside the parsed message.

Week 15 is EOA-only. Supporting ERC-1271 contract accounts would require a
chain-specific RPC verification boundary and session invalidation rules because
contract signature validity can change with chain state.

## Atomic one-time consumption

Signature validity alone does not prevent replay. `ISiweChallengeStore` owns the
atomic transition from issued to consumed. The in-memory implementation keeps
that transition under one lock, so 24 concurrent submissions of the same valid
signature produce exactly one success and 23 `ChallengeAlreadyUsed` results.

Verification order is deliberate:

1. parse canonical message;
2. compare active relying-party policy;
3. recover and compare the EOA signer;
4. atomically find, compare, expire, and consume the exact server challenge.

A wrong domain, URI, chain, statement, time, or signature does not burn the
original challenge. The user may still submit the correctly signed message.

The in-memory store is bounded and prunes consumed or expired entries when it
needs capacity. It is not durable and cannot coordinate two processes. After a
pruned replay it may report `ChallengeNotFound` instead of
`ChallengeAlreadyUsed`, but both outcomes reject authentication.

## Failure matrix

| Input or state | Result | Challenge remains usable? |
| --- | --- | --- |
| malformed/noncanonical message | `MalformedMessage` | yes |
| wrong domain, URI, chain, statement, or time facts | `PolicyMismatch` | yes |
| malformed signature or different recovered address | `InvalidSignature` | yes |
| nonce issued by another store | `ChallengeNotFound` | n/a |
| expired nonce | `ChallengeExpired` | no valid use remains |
| first exact valid proof | success | no; atomically consumed |
| repeated or concurrent exact proof | `ChallengeAlreadyUsed` | no |
| active challenge capacity exhausted | `ChallengeCapacityExceeded` | existing challenges unchanged |

## Residual limitations

This milestone still has no:

- HTTP challenge or verification endpoint;
- durable/multi-process challenge store;
- browser/session binding, secure cookie, CSRF control, logout, or revocation;
- user, tenant, role, scope, policy engine, or authorization check;
- trusted reverse-proxy/origin-header policy;
- ERC-1271 contract wallet, ENS lookup, resources, request ID, or not-before;
- rate limit, abuse prevention, audit log, privacy review, or production hosting.

The class library must remain process-local learning code until those boundaries
arrive with their own tests. A valid result must never be treated as permission
to move funds.

## Verification coverage

Focused tests cover canonical render/parse, malformed variants, unsafe origins,
mainnet rejection, statement bounds, stable policy fingerprint, valid recovery,
wrong signer, invalid signature shape and recovery ID, cross-domain/cross-chain/
cross-URI/cross-statement attempts, shifted time facts, expiry, foreign stores,
capacity recovery, exact replay, and 24-way concurrent consumption.

Final committed-snapshot counts and CI links are recorded after the milestone
commit passes the complete verification path.

## Suggested reading order

1. `SiweAuthenticationPolicy.cs` for relying-party authority and limits.
2. `SiweChallenge.cs` and `SiweMessage.cs` for signed facts and rendering.
3. `SiweMessageParser.cs` for canonical input handling.
4. `SiweEoaSignatureVerifier.cs` for ERC-191 recovery.
5. `InMemorySiweChallengeStore.cs` for one-time atomic consumption.
6. `SiweAuthenticationService.cs` for verification order.
7. Authentication tests for executable failure and concurrency examples.

## What should come next

Week 16 should replace the process-local challenge state with a separately
migrated SQLite store. It should prove restart persistence, atomic shared-file
consumption, expiry cleanup, schema constraints, and strict retry behavior
before any HTTP login endpoint is added.
