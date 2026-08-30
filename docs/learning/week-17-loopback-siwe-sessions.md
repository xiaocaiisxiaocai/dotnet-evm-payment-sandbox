# Week 17: loopback SIWE login and opaque browser sessions

Week 17 composes the strict Week 15 proof and durable Week 16 challenge store
into a bounded HTTP authentication boundary. It adds browser-flow binding,
opaque sessions, rotation, CSRF-protected logout, revocation, and restart-safe
SQLite state.

This remains a local learning boundary. It is not a public identity service,
user directory, role model, payment authorization system, or production browser
deployment.

## HTTP contract

The API maps four endpoints:

| Method and path | Required boundary | Result |
| --- | --- | --- |
| `POST /v1/auth/siwe/challenge` | loopback, exact configured `Origin`, non-zero address | canonical message plus an HttpOnly flow cookie |
| `POST /v1/auth/siwe/verify` | loopback, exact `Origin`, flow cookie, message, signature | rotated session and CSRF cookies |
| `GET /v1/auth/session` | loopback and one session cookie | authenticated address/chain and bounded timestamps |
| `POST /v1/auth/logout` | loopback, exact `Origin`, session cookie, matching CSRF cookie/header | one-way revocation and cookie deletion |

Every response is `no-store`. Authentication failures use generic responses;
they do not reveal whether a nonce, browser binding, session, expiry, signature,
or revocation check failed.

The requested address on the challenge endpoint is display input only. It is
not trusted until ERC-191 recovery proves that the message signer equals that
address.

## Trusted origin and loopback

The relying-party origin, request URI, chain, statement, lifetimes, database
paths, and capacities come from server configuration. Request `Host` and
`Origin` never become signed facts.

All authentication endpoints require a loopback remote address. Each
state-changing endpoint also requires exactly one `Origin` header equal to the
configured HTTPS origin. Missing, repeated, differently cased, slash-suffixed,
or otherwise different values fail closed.

Loopback is a deployment restriction, not an authentication factor. Malware on
the same machine, a hostile local proxy, DNS/rebinding mistakes, and a browser
extension remain separate threats. Rate limiting and a production host policy
are not implemented.

## Why the challenge needs a separate browser secret

A SIWE nonce prevents proof replay, but the nonce and message are visible to the
page and wallet. Challenge issuance therefore generates an independent random
256-bit flow token, places the raw value only in an HttpOnly cookie, and stores
only its SHA-256 hash beside the nonce.

Verification checks this binding before signature verification and before
consuming the SIWE challenge. A missing, malformed, repeated, or wrong flow
cookie returns a generic `401` without burning the correct proof. This prevents
another browser context that merely obtains the message/signature pair from
completing the flow.

The flow is still not a complete anti-phishing design. The user must inspect the
wallet's domain, URI, chain, statement, and address; a compromised page or
wallet remains outside this boundary.

## Opaque session and CSRF credentials

Successful verification generates two independent 256-bit random values:

- the session bearer token is stored in an HttpOnly cookie; and
- the CSRF token is stored in a readable cookie so the client can copy it into
  `X-CSRF-Token` for logout.

Both are lowercase hexadecimal solely to make one canonical transport shape.
SQLite stores only SHA-256 hashes. Exceptions and record `ToString()` methods do
not retain or print raw credentials.

All three cookies use:

- a `__Host-` name;
- `Secure`;
- `SameSite=Strict`;
- `Path=/`; and
- no `Domain` attribute.

The flow and session cookies are HttpOnly. The CSRF cookie intentionally is not,
because double-submit logout needs the client to copy its value into a header.
The service compares cookie and header in fixed time, then the store matches the
CSRF hash belonging to that session.

## Rotation and revocation transaction

A fresh wallet proof always creates fresh session and CSRF tokens. If a valid
prior session cookie is present, one immediate SQLite transaction:

1. revalidates the unused browser flow;
2. tentatively revokes the prior session;
3. cleans only expired/revoked sessions if the database-owned capacity is full;
4. inserts the new hashed credentials;
5. consumes the flow one way; and
6. commits all changes together.

If capacity is still full or a generated hash collides, the transaction rolls
back. The old session therefore remains active unless its replacement exists.
A present but malformed or duplicate prior-session cookie is rejected rather
than silently skipping rotation.

Logout likewise changes `revoked_at` from null once. A schema trigger prevents
credential/fact mutation, un-revocation, or a second revocation update. Session
lookup treats the exact expiration timestamp as expired.

## Migration 2 and database-owned limits

Migration 2 upgrades the Week 16 authentication database in place. It adds:

- a one-time initialized, then immutable `session_capacity` setting;
- `siwe_login_flows` with unique binding hashes, expiry, and one-way use;
- `siwe_sessions` with unique session/CSRF hashes, recovered address/chain,
  created/expiry timestamps, and one-way revocation; and
- expiry indexes and `STRICT`/`CHECK` constraints.

A Week 16 database already contains its challenge-capacity row. Migration 2
adds a nullable session-capacity column so the first Week 17 startup can fill it
exactly once; later mismatches fail startup. This is a reviewed upgrade path,
not permission for runtime configuration drift.

The session count includes active rows. Cleanup happens only at capacity and
removes expired or revoked rows. Separate hosts and separate files still do not
coordinate.

## The deliberate two-transaction availability gap

Canonical proof verification consumes the Week 16 challenge through
`ISiweChallengeStore`. Session creation then uses the browser-session store.
These abstractions do not share one transaction.

If the process crashes or session insertion fails after challenge consumption
but before session commit, no unauthorized session can appear; the user must
request and sign another challenge. This is a safe availability failure, not
exactly-once login delivery.
A future production design would need an explicit outbox/state-machine review
before claiming stronger delivery semantics.

## HTTP test transport versus browser deployment

The integration tests start real Kestrel on an ephemeral
`http://127.0.0.1` address and manually carry `Set-Cookie` values. That lets the
tests inspect `Secure` attributes without weakening the production cookie
contract.

An ordinary browser deployment must provide reviewed HTTPS and a compatible
same-site origin/host arrangement before these cookies can work. This milestone
does not add a development exception that removes `Secure`, relaxes
`SameSite=Strict`, enables broad CORS, or derives trust from an incoming host.

## Executable evidence

Focused service and persistence tests cover:

- restart-safe flow and session lookup;
- raw bearer/CSRF values never being stored;
- wrong binding not consuming the correct proof;
- concurrent proof consumption producing one session;
- transactional login rotation;
- double-submit CSRF and one-way logout;
- exact expiration; and
- capacity failure preserving an unrelated active session.

Real Kestrel tests additionally cover exact Origin rejection, hardened cookie
attributes, duplicate flow-cookie rejection, flow verification after restart,
session lookup after another restart, HTTP rotation/logout, and generic expired
session responses.

Formal committed-snapshot and GitHub Actions evidence will be added after the
implementation commit is independently verified.

## Residual limitations

Week 17 deliberately has no:

- public hosting, TLS certificate/proxy recipe, production CORS, browser UI, or
  end-to-end wallet connector;
- rate limit, account/IP throttling, tenant isolation, user/role/permission
  model, MFA, recovery, or privacy-approved audit log;
- session renewal, idle timeout, device list, global logout, or cross-host
  revocation;
- database encryption, backup, independent tamper evidence, or distributed
  consistency;
- ERC-1271 contract-wallet support; or
- connection between an authenticated address and Payment Intent creation,
  Router transfer, transaction signing, reconciliation, or settlement.

The existing Payment Intent endpoints intentionally remain unauthenticated.
Making them depend on a SIWE address would require an authorization and tenant
model; authentication alone cannot safely invent that policy.

## Suggested reading order

1. `SiweAuthenticationEndpoints.cs` for HTTP/origin/cookie failure behavior.
2. `SiweBrowserSessionService.cs` for proof composition and credential redaction.
3. `ISiweBrowserSessionStore.cs` for the transaction contract.
4. `SqliteSiweBrowserSessionStore.cs` for flow use, rotation, capacity, and
   revocation SQL.
5. `SiweChallengeDatabaseMigrations.cs` for migration 2 constraints/triggers.
6. Browser-session and API HTTP tests for restart, race, and failure evidence.
7. The Week 15 and Week 16 guides for the proof and durable challenge layers.
