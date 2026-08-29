# Week 6: Payment Intent API

Week 6 adds the first runnable .NET application. Its purpose is deliberately
narrow: accept an off-chain request to receive a token payment, assign a public
correlation ID, and make retries safe within one process. It does not contact an
RPC endpoint, create calldata, ask a wallet to sign, broadcast a transaction,
observe a log, or claim settlement.

## Boundary first

A returned `status` of `created` means only that the API accepted and retained
an intent in its current process. It does not mean any of the following:

- the merchant address belongs to the expected business;
- the token is supported or even deployed on the requested chain;
- a payer authorized a transfer;
- a transaction exists, succeeded, or reached finality;
- the merchant received the requested amount.

Those statements require policy, wallet, RPC, indexing, token-transfer, reorg,
and reconciliation evidence that is intentionally absent this week.

## HTTP contract

The application exposes three endpoints:

| Method and route | Meaning | Success |
| --- | --- | --- |
| `POST /v1/payment-intents` | Create or safely replay an intent | `201 Created` for the first request, `200 OK` for a safe replay |
| `GET /v1/payment-intents/{paymentId}` | Read a process-local intent | `200 OK` |
| `GET /health/live` | Prove that the HTTP process can respond | `200 OK` |

Create requires exactly one case-sensitive `Idempotency-Key` header containing
1-128 visible ASCII characters. The JSON body is:

```json
{
  "chainId": "31337",
  "tokenAddress": "0x2222222222222222222222222222222222222222",
  "merchantAddress": "0x3333333333333333333333333333333333333333",
  "amountRaw": "1250000"
}
```

`chainId` and `amountRaw` are JSON strings on purpose. JavaScript numbers are
not exact above `2^53 - 1`, while both values may occupy the EVM `uint256`
range. Addresses are syntactically validated and normalized to lowercase. This
is not an EIP-55 checksum or an on-chain existence check.

All intent responses use `Cache-Control: no-store`. A malformed create or ID
returns `400`; an unknown but well-formed ID returns `404`. Reusing a key with
different terms returns `409` and does not disclose the original payment ID.
The Kestrel request-body limit is 16 KiB, so oversized bodies fail with `413`
before the application service can mutate state.

## Normalized business idempotency

Idempotency is not merely “return whatever was created for this header.” The
store compares the normalized business terms attached to the key:

| Existing key | Incoming normalized terms | Result |
| --- | --- | --- |
| No | Any valid terms | Store once, return `201`, `Idempotency-Replayed: false` |
| Yes | Equal | Return the original object, `200`, `Idempotency-Replayed: true` |
| Yes | Different | Return non-leaking `409 idempotency_key_reused` |

For example, chain IDs `031337` and `31337`, raw amounts `0001` and `1`, and
upper/lowercase forms of the same address compare as equal after parsing. The
idempotency key itself is not normalized: `order-a` and `ORDER-A` are different
keys.

The in-memory store has two indexes, one by key and one by `PaymentId`. A single
lock protects lookup and publication in both indexes. This is important because
a check-then-insert implemented with unrelated concurrent dictionaries could
publish two resources during a race. The tests send 20 real concurrent HTTP
requests and require exactly one `201`, 19 replays, and one shared response.

The future database replacement must preserve this behavior with a unique key
constraint and one transaction. An application-side existence query followed
by an insert is not equivalent.

## Code reading order

1. `Domain/Evm/EvmAddress.cs` and `EvmChainId.cs` define canonical EVM-shaped values.
2. `Domain/PaymentIntents/PaymentIntentTerms.cs` groups immutable create facts.
3. `Domain/PaymentIntents/PaymentIntent.cs` defines the intentionally small state model.
4. `Api/PaymentIntents/CreatePaymentIntentRequest.cs` translates untrusted JSON into Domain values.
5. `Api/PaymentIntents/PaymentIntentService.cs` creates a candidate without owning persistence details.
6. `Api/PaymentIntents/InMemoryPaymentIntentStore.cs` owns the atomic idempotency decision.
7. `Api/PaymentIntents/PaymentIntentEndpoints.cs` maps outcomes to HTTP semantics.
8. `PaymentIntentHttpTests.cs` verifies the boundary through an actual loopback Kestrel server.

The API references Domain but deliberately does not reference Contracts. Intent
creation does not need RPC identity or calldata, so coupling those operations
would enlarge the failure and trust boundary without adding a truthful result.

## Run locally

```powershell
dotnet run --project .\src\PaymentSandbox.Api --urls http://127.0.0.1:5086
```

In another PowerShell session:

```powershell
$headers = @{ 'Idempotency-Key' = 'local-checkout-1' }
$body = @{
    chainId = '31337'
    tokenAddress = '0x2222222222222222222222222222222222222222'
    merchantAddress = '0x3333333333333333333333333333333333333333'
    amountRaw = '1250000'
} | ConvertTo-Json

$created = Invoke-RestMethod `
    -Method Post `
    -Uri http://127.0.0.1:5086/v1/payment-intents `
    -Headers $headers `
    -ContentType application/json `
    -Body $body

Invoke-RestMethod `
    -Uri "http://127.0.0.1:5086/v1/payment-intents/$($created.paymentId)"
```

## Tests and residual limits

The tests cover value boundaries, canonical serialization, first create, safe
replay, conflicting reuse, cancellation before mutation, concurrent requests,
field-specific validation, malformed and oversized JSON, missing/repeated
headers, malformed/unknown IDs, and health response behavior.

This remains a local learning boundary. The store is lost on restart, cannot
coordinate two API instances, has no expiry or capacity policy, and contains no
authentication, authorization, tenant isolation, rate limiting, durable audit
trail, token allowlist, database, or privacy classification. The API must stay
loopback/test-only until later work explicitly closes those gaps.

Week 7 should replace the volatile store with a migration-owned SQLite model and
preserve the same idempotency transaction semantics. It should not add chain
settlement states before the indexer can support them with canonical evidence.
