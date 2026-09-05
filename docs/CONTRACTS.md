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

## M2 replay contract

POST /deliveries/{id}/replay is introduced only in M2. Permit an exhausted Failed delivery; reject replay while active or already Delivered. Use a replay idempotency key so an ambiguous replay response can be retried safely. Atomically advance the replay/work identity and insert new outbox work while preserving DeliveryId and history. Specify the final request/response details as a small M2 contract extension before coding them.

## Configuration and security

Configuration identifies database, queue, destination and operational bounds. Generate actual names/examples alongside M1 code; .env examples contain no usable credentials. Local demo access is loopback-only. Cloud ingress authentication, workload identities and webhook signing/access controls are M3 acceptance requirements, not claims about M1.
