# Initial contracts

These are ordinary application contracts, not a binary auditing protocol. Keep API, SQL semantics, tests and docs aligned. Internal representations remain implementation choices.

## Event submission

POST /events accepts application/json and a required Idempotency-Key header. The initial request is:

```json
{
  "destinationId": "demo",
  "eventType": "document.ready",
  "data": { "documentId": "doc-001" }
}
```

The one supported destination resolves from operator configuration. No URL is accepted from this request. The event type is document.ready. Nonempty identifiers and key are at most 128 characters; use visible ASCII keys without whitespace. Impose a small explicit request-body limit and document it when implemented. Reject unknown properties with the built-in serializer's standard support; no custom parser.

Identity compares the validated destinationId, eventType and documentId with ordinal case-sensitive semantics. JSON formatting/property order does not change identity. Store the typed values; do not introduce a custom canonical hash format. SQL key uniqueness must agree with the case-sensitive comparison.

M1 implementation bounds: 4096 request bytes, including chunked requests; property names are case-sensitive and unknown properties are rejected by System.Text.Json. The SQL idempotency key is varchar(128) with Latin1_General_100_BIN2 collation. Validation excludes whitespace, including trailing spaces, so SQL padding cannot collapse distinct valid keys. Typed document values are compared with StringComparison.Ordinal after retrieval.

| Condition | HTTP result |
| --- | --- |
| New valid key/input committed | 202, deliveryId and status, Location pointing to status |
| Existing key with identical input | 200, original deliveryId and current status |
| Existing key with conflicting input | 409 with a stable machine-readable error code |
| Invalid request or destination | 400 with field errors |
| Body too large | 413 |
| Wrong content type | 415 |
| Persistence unavailable, no confirmed acceptance | 503; caller may retry the same key |

If the transaction committed but the HTTP response was lost, a repeat with the same key resolves the result. Do not imply that a failed connection proves the transaction rolled back.

Use standard ProblemDetails responses plus a concise error code. Do not return connection strings, internal paths or stack traces.

## Status and attempts

GET /deliveries/{id} returns 200 for a known ID, 404 for an unknown valid ID and 400 for malformed input. Include logical ID, status, accepted/updated timestamps, relevant attempts and a safe last-outcome description.

| Status | Meaning |
| --- | --- |
| Pending | Accepted; eligible work has not completed |
| Processing | An attempt owns a time-bounded claim |
| Delivered | A success acknowledgement was received and persisted |
| Failed | The milestone's applicable attempt/retry budget ended without confirmed delivery |

Status is sender knowledge. Delivered means acknowledged, not semantic correctness of an arbitrary recipient. Failed does not prove the recipient had no effect. For timeouts, lost responses or interrupted attempts, make the remote outcome unknown where appropriate. Show stale/incomplete processing honestly; do not invent a success or hide broker dead-letter exhaustion.

Each HTTP attempt has its own identifier, start/end (when known), outcome/status code and safe failure category. Preserve a started-but-interrupted attempt. Bound returned history; introduce pagination when needed in M2 rather than silently dropping facts.

M1 returns the latest 50 attempts, `attemptCount` and explicit `historyTruncated`. All attempts remain in SQL. Timestamps are UTC with an explicit JSON UTC suffix. `leaseExpired` and `warning` expose interrupted/incomplete work; the API does not infer actual broker state. Inspect dead letters separately. `Acknowledged`, `ResponseReceived`, `Unknown` and `NotAttempted` describe remote knowledge; a non-2xx response does not establish absence of an effect.

## Queue and webhook

The queue is an internal work signal. It carries the stable DeliveryId and sufficient version/work identity to reject stale work. Outbox retries reuse an outbox item's MessageId. M2 scheduled attempts/replay can create a new work identity without changing the logical DeliveryId. Envelope serialization uses standard JSON and ordinary versioning.

Send the event body to the configured receiver with X-RelayLab-Delivery-Id. A 2xx response is success. M1 records one normal HTTP failure as Failed; recovery policy is extended in M2. Disable automatic HTTP retries that would create unrecorded attempts. Configure a total attempt deadline, response-size limit and bounded parallelism.

M1 defaults: 10-second total HTTP deadline, 30-second SQL lease, two concurrent handlers (configurable maximum four). Redirects and cookies are disabled. Responses are status/header acknowledgements: at most 16 KiB of headers, zero response-body bytes read/buffered/drained. No HTTP retry handler is installed. Service Bus SDK retries are disabled with a five-second try timeout. Outbox sends have a ten-second deadline and a 30-second lease, retry at a fixed three-second eligible interval after transport failure, and reuse the outbox UUID as MessageId. Queue PeekLock duration is one minute, automatic renewal at most two minutes, MaxDeliveryCount ten, TTL one hour, and duplicate detection disabled to exercise durable recipient deduplication.

The sample receiver atomically inserts a unique receipt keyed by DeliveryId and its example effect. Identical duplicates acknowledge the existing receipt. A duplicate ID with conflicting content is rejected. The delivery service never queries the receiver ledger to guess the outcome; the test harness may inspect it.

## M2 retries, recovery and replay

M2 supersedes M1's single-attempt policy. Acceptance persists a budget (default 3, configurable 1–5) and base retry delay (default 2 seconds, configurable 1–30). Each Started record consumes one attempt, including an interrupted attempt whose HTTP outcome is unknown. Retry transport errors, timeouts, HTTP 408/429 and 5xx; other non-2xx responses end the generation immediately. Backoff is `min(30, baseSeconds * 2^(attemptNumber-1))`, measured from the durable result/recovery transition using SQL UTC. There are no hidden HTTP retries. Retry-After is not used. A terminal nonretryable failure may also be explicitly replayed.

DeliveryId is permanent. Each replay increments `generation` (initially 0), resets `attemptNumber` to 1, and retains the original budget and history. Each attempt slot has a unique WorkId/outbox row; each HTTP execution has a unique AttemptId and globally increasing history sequence. An outbox has at most one Started attempt. The worker claims only the current, eligible Pending slot. A busy, early or stale signal can be completed safely because SQL retains the authoritative current intent. A stale lease owner cannot commit either an outcome or retry intent. Expired Processing claims become Interrupted/Unknown and consume their slot; recovery atomically creates the next eligible work item or marks Failed if exhausted. An interrupted attempt has no invented completion timestamp.

Workers scan SQL once per second. The outbox publishes eligible current Pending work, reusing that work's MessageId after ambiguous sends and periodically (default 30 seconds, configurable 5–300) while it remains Pending. Publication transport retries do not consume HTTP attempts. This reconciliation also recovers work whose signal expired or dead-lettered; it does not delete or pretend to inspect dead letters. Malformed/unknown signals remain in the DLQ for inspection; stale valid signals are harmlessly completed. Queue duplicate detection is disabled in the supplied setup. With broker duplicate detection enabled elsewhere, recovery can be delayed by its detection window. SQL intent is retained indefinitely; service/dependency recovery and retained SQL are prerequisites for eventual progress. Emulator restart loses broker data and is not evidence of Azure durability.

`POST /deliveries/{id}/replay` has no body and requires one `Idempotency-Key` with the event-key syntax. The key is scoped to the delivery. A first request for Failed atomically inserts a replay receipt, advances the generation/current work and inserts outbox intent, returning 202 plus Location. A repeated key returns 200 and the original `generation` and `workId`, even after later progress; `status` is the delivery's current status. A new key while Pending, Processing or Delivered returns 409 `replay_not_allowed`; concurrent distinct keys have at most one winner. Unknown ID is 404, malformed ID/key or nonempty body is 400. Ambiguous persistence returns 503 and is resolved by repeating the same key. DeliveryId and previous attempts never change or disappear.

Status includes current WorkId, generation, attempt number/budget, next eligibility and remaining slots. `GET /deliveries/{id}?after=0&limit=50` pages attempts in increasing sequence order; after is a nonnegative exclusive cursor and limit is 1–50. `nextCursor` is present when another page exists; `attemptCount` is the total retained count and `historyTruncated` explicitly indicates further pages. Separate queries are a live view, not a transactionally frozen history snapshot.

## Configuration and security

Configuration identifies database, queue, destination and operational bounds. Generate actual names/examples alongside M1 code; .env examples contain no usable credentials. Local demo access is loopback-only. Cloud ingress authentication, workload identities and webhook signing/access controls are M3 acceptance requirements, not claims about M1.

## M3 production boundary

Production ingress requires a valid single-tenant v2 Entra app token: RS256 signature, configured audience/issuer, lifetime, tenant/object ID, `idtyp=app` and no delegated `scp`. API callers are the deployment workload identity; receiver webhooks require the worker identity, while receipt diagnostics require the separate deployment identity. Missing/invalid tokens yield 401 and authenticated callers outside the ACL yield 403. Only minimal health endpoints are anonymous. Resource app token issuance alone confers no API permission. Local Development/Testing remains loopback-only and unauthenticated.

The worker sends an audience-specific managed-identity bearer token over HTTPS only to the exact configured receiver URI. Redirects remain disabled; no shared signing secret or fault-control ingress is added. SQL and Service Bus use explicit managed identities; runtime principals have only the documented table/queue permissions. All M2 identity, transaction, attempt and ambiguity contracts remain unchanged.

Cloud M3 schema version 1 is an explicit **fresh** baseline with database-kind/model-hash verification. It does not upgrade unversioned M1/M2 data. Runtime startup cannot initialize schema. Application rollback is restricted to compatible M3 images and preserves the database; no universal exactly-once guarantee or destructive schema rollback is introduced. [CLOUD.md](CLOUD.md) records the network boundary, exact access and limitations requiring Azure execution.
