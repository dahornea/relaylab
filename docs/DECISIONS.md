# Decision notes

Keep this file short. Record only choices whose rationale a maintainer needs, with their impact and actual verification. Do not create an ADR for each implementation detail.

## Initial selected choices

| Choice | Reason | Limit |
| --- | --- | --- |
| API and Worker, one shared application database | Separate request lifetime from asynchronous work | Not a general microservice platform |
| SQL transactional outbox | Persist delivery intent with acceptance | Broker publication can repeat |
| Official Service Bus SDK and small application-specific dispatcher | Make the failure boundaries inspectable | Not a reusable framework claim |
| Durable receiver receipt and effect | Demonstrate recipient-side idempotency | Requires a cooperating recipient |
| Compose and Testcontainers | Developer startup and isolated automated integration | Emulator is not the Azure service |
| GitHub Actions, then Terraform/Azure | Show the delivery/operations lifecycle publicly | Remote/cloud claims need real runs |
| Existing telemetry viewer | Show diagnostic evidence with low UI effort | Optional custom inspector is unscheduled |

## M1 decisions

- Use the installed SDK 10.0.400 with latestPatch roll-forward, EF Core 10.0.11, Azure.Messaging.ServiceBus 7.20.2 and Testcontainers 4.14.0. xUnit 2.9.3 with its VSTest adapter 3.1.5 and Microsoft.NET.Test.Sdk 18.9.0 supplies one runner. Lock files record transitive versions; `eng/verify.ps1` uses locked restore. This compatible runner avoids mixing MTP and VSTest conventions.
- SQL key uniqueness uses varchar(128), Latin1_General_100_BIN2 and validation excluding whitespace/nonvisible ASCII. Typed identity comparisons use ordinal .NET string equality. An explicit transaction commits two writes; SQL uniqueness resolves concurrent first submissions. A test-only SQL trigger forces a failure after the delivery write to exercise real rollback.
- Claims use conditional SQL updates, random owner tokens and SQL SYSUTCDATETIME. HTTP results require the same current work and unexpired owner inside a short transaction with the attempt update. Started-but-interrupted attempts retain an unknown end time and are marked Interrupted on recovery. Local code never holds a transaction across HTTP or Service Bus.
- One outbox UUID is the initial work identity and broker MessageId. The direct SDK makes the non-atomic SQL/broker boundary visible without adding a general messaging framework. Outbox transport retries are durable; ordinary HTTP retries/replay are absent in M1. See contracts for exact bounds.
- `EnsureCreated` runs only through explicit local `--init-db` commands and test setup; application requests do not migrate schema. It is sufficient for this first local schema. Versioned migrations and deployment coordination must be designed before M3; do not use EnsureCreated to evolve an existing schema.
- Compose and Testcontainers read the same queue configuration and image inventory. SQL 2022 CU14 is the concrete compatibility baseline used by the Testcontainers Service Bus builder, not a claim of the latest SQL security patch. SQL databases for RelayLab, receiver and emulator remain distinct on one local SQL instance. The emulator is pinned to 2.0.0 and loses messages on restart.
- Status exposes possible incomplete/broker-exhausted work without claiming dead-letter reconciliation. The worker's read-only `--deadletters` command preserves messages. Full reconciliation and retention/paginated history are M2 decisions.
- All ingress is local; executable startup rejects the default Production environment. Default Compose publishes only loopback ports and uses an ignored generated local password. Payloads, SQL exception details and destination URLs are excluded from application logs. No signing, cloud identity or production-security claim is made.

## M2 decisions

- SQL is the scheduler. A Delivery points to one current outbox/work slot; unique `(DeliveryId, Generation, AttemptNumber)` outbox rows and unique attempt WorkId enforce its identity. Attempts have separate UUIDs and increasing SQL history sequences. New retry/replay intent commits with its delivery transition; repeated publication retains a slot's MessageId. There is no extra scheduler service or broker scheduled-message dependency.
- Persist max attempts and base delay at acceptance. Interrupted Started attempts consume the same budget as HTTP failures. Retry timeouts/transport failures, 408/429 and 5xx with bounded exponential delay; other non-2xx responses stop immediately. Any terminal Failed generation can be explicitly replayed. The complete numerical bounds and response rules are in CONTRACTS.
- An owner-token/current-work/SQL-time conditional update fences both result and attempt writes. Recovery requires an expired lease, while normal completion requires an unexpired lease. Recovery preserves an interrupted attempt's unknown end time. A recovery transaction either advances work plus outbox or exhausts; no transaction spans HTTP. If SQL rolls back a retry insertion, the original Started claim remains recoverable.
- Pending current work is periodically republished even after a successful send. Completing a busy, early or stale signal cannot remove SQL intent. This recovers valid current work from the broker DLQ without reading/deleting it or conflating transport exhaustion with HTTP budget exhaustion. Invalid/unknown messages remain diagnostic dead letters. No finite broker retention is treated as the source of accepted intent.
- Replay requests lock one delivery row before receipt lookup. This serializes same/different-key races and allows a repeated key to return its original generation/work even after completion. It avoids optimistic retry loops and keeps SQL transactions short.
- Failure tests use internal inert async boundary observers, a test-owned TCP gate and SQL-time eligibility/lease changes. They run the real SDK, SQL, Kestrel and emulator. The demo additionally kills the actual worker container after observing a durable receiver effect. These controlled local experiments do not establish arbitrary timing/OS/network/Azure correctness.
- OpenTelemetry 1.18.0 exports manual lifecycle spans and bounded-label metrics to standalone Aspire dashboard 13.5.2. The trace parent persists on Delivery, so retry/replay/recovery spans share the accepted trace across processes. Traces and metrics use one instance ID per process; delivery/work IDs are trace attributes, never metric labels. No payload, idempotency key, document ID or destination URL is emitted. Export buffering may lose diagnostics on SIGKILL; SQL remains authoritative. The demo waits for the exhausted generation's trace before killing its exporter.
- Local M2 uses a fresh schema. Explicit initialization rejects an M1 schema instead of silently treating EnsureCreated as an upgrade. Preserve existing M1 databases separately; destructive reset is an explicit local operator command. Controlled migrations remain M3 work. This limitation concerns upgrading M1 data, not restarting M2 processes against retained M2 SQL.

## Resolve when M3 is requested

Actual Azure resource/SKU plan, ingress/recipient authentication, identity scoping, migrations, rollback and retention/teardown. Paid execution and external access changes require concrete authorization.
