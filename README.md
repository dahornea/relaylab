# RelayLab

A .NET 10 webhook delivery service demonstrating SQL acceptance and outbox persistence, Azure Service Bus messaging, bounded durable retries, idempotent replay, interruption recovery and OpenTelemetry.

The API and worker share SQL state. A separate sample receiver atomically records a receipt and its effect using the permanent delivery ID. HTTP can repeat; this cooperating receiver demonstrates one effect per delivery in the tested scenarios. RelayLab does not promise universal exactly-once delivery.

## Run the failure and recovery demo

Prerequisites: .NET SDK **10.0.400** (or a compatible patch), **PowerShell 7**, Git, and Docker Desktop with **Linux containers**. Allow at least 6 GB for SQL, Service Bus and Aspire. The first run downloads the pinned Microsoft images and NuGet packages. SQL Developer and the Service Bus emulator are local development dependencies; Compose/Testcontainers configure their license acceptance.

**Schema requirement:** M2 needs a fresh local database schema. It cannot upgrade an M1 database; initialization rejects that schema without erasing it. Preserve existing data in its own database/project. Verification always uses isolated disposable resources. See the M1 database upgrade note below before reusing an older demo project.

From PowerShell 7 in your RelayLab checkout:

```powershell
pwsh -NoProfile -File .\eng\demo.ps1
```

The script generates an ignored random SQL password, builds the applications and initializes fresh local databases. It verifies a normal event (`202`, identical repeat `200`, Delivered, one effect), then demonstrates a second delivery:

1. The receiver returns 503. Three persisted attempts exhaust the generation.
2. Replay returns 202; repeating its key returns 200 with the same work identity.
3. The receiver commits its effect and withholds the acknowledgement. The script kills the actual worker process with SIGKILL while SQL still reports Started.
4. The receiver and worker restart against their retained databases. SQL lease recovery preserves the interrupted/unknown attempt and schedules another work item. The receiver acknowledges the duplicate without repeating its effect.
5. The final delivery is Delivered with five historical attempts, generation 1 and one receiver effect. The script queries Aspire and verifies the correlated recovery trace across API, worker and receiver.

Evidence is under `artifacts/demo/`: exhausted/interrupted/recovered status JSON, recovery trace and compact result JSON. The demo waits for required observable states and fails if the kill misses its intended boundary. Use `-Scenario Happy` for only the ordinary delivery flow.

| Local endpoint | Address |
| --- | --- |
| API | http://127.0.0.1:5080 |
| Sample receiver | http://127.0.0.1:5081 |
| Aspire dashboard | http://127.0.0.1:18888 |
| Emulator health | http://127.0.0.1:5300/health |

SQL, AMQP and OTLP remain inside the Compose network. Aspire is anonymous on loopback for this local demo. Applications require Development/Testing; public ingress and cloud authentication are not implemented.

**M1 database upgrade:** M2 needs a fresh local schema; `--init-db` rejects the earlier M1 schema. Preserve old data in its existing database/project. `eng/verify.ps1` always creates an isolated fresh project and leaves existing projects alone. If old demo data is disposable, explicitly reset that project's volume before running M2. Reset is not a migration or a rollback strategy.

## Submit, inspect and replay

```powershell
$body = '{"destinationId":"demo","eventType":"document.ready","data":{"documentId":"doc-002"}}'
$delivery = Invoke-RestMethod http://127.0.0.1:5080/events -Method Post `
  -ContentType application/json -Headers @{ 'Idempotency-Key' = 'example-002' } -Body $body
Invoke-RestMethod "http://127.0.0.1:5080/deliveries/$($delivery.deliveryId)?after=0&limit=50" | ConvertTo-Json -Depth 8
Invoke-RestMethod "http://127.0.0.1:5081/receipts/$($delivery.deliveryId)"

# Only a Failed delivery permits a new replay; repeat the same key after a lost response.
Invoke-RestMethod "http://127.0.0.1:5080/deliveries/$($delivery.deliveryId)/replay" `
  -Method Post -Headers @{ 'Idempotency-Key' = 'replay-example-002' }
```

Default budget: three Started attempts per generation, including interruptions. Retry timeouts, transport errors, HTTP 408/429 and 5xx with persisted exponential backoff (2, 4… seconds, capped at 30). Other non-2xx responses stop that generation immediately. Replay preserves the delivery ID and history. A repeated replay key identifies its original generation/work even after delivery completes. See [contracts](docs/CONTRACTS.md) for bounds, errors and cursor pagination.

`Failed` is sender knowledge: the receiver may already have committed an effect. Unknown outcomes stay visible in attempt history. SQL fencing protects newer owner state; it cannot undo an external HTTP effect.

## Inspect telemetry and recovery

In Aspire, open **Traces** and filter on `@relaylab.delivery.id:<deliveryId>` from `recovery-result.json`. The trace contains acceptance, publication, attempts, HTTP, receiver, replay and recovery spans. A killed process can lose buffered spans; SQL records remain authoritative.

Under **Metrics**, select `relaylab-worker`, then `relaylab.attempt.outcomes`, `relaylab.attempt.started`, `relaylab.pending`, `relaylab.publications` or `relaylab.http.duration`. Use **Table** for numeric values; outcome tags distinguish rejected, interrupted and acknowledged transitions. API acceptance/replay and receiver effect counters are on their respective resources. Pending is a SQL snapshot per worker: do not sum it across replicas. Metrics have bounded labels, never delivery IDs. The viewer keeps data in memory and loses it on restart.

```powershell
pwsh -NoProfile -File .\eng\demo.ps1 -Action DeadLetters
docker compose logs --tail 100 worker
```

Dead-letter inspection peeks at the first 50 messages and removes nothing. Broker exhaustion differs from application exhaustion: SQL periodically republishes eligible current Pending work, using the same work MessageId. An expired Processing lease consumes its interrupted slot and schedules another or ends Failed. No accepted intent is intentionally deleted from SQL. Progress still requires retained SQL and dependencies becoming available; a permanently failing receiver needs operator action.

The emulator loses messages on restart. M2's SQL reconciliation can recreate current work signals, but this does not demonstrate Azure broker durability. The tests interrupt connectivity while retaining the emulator process and its messages.

## Verify M2

```powershell
pwsh -NoProfile -File .\eng\verify.ps1
```

This runs locked restore, a Release build with warnings as errors, all xUnit/VSTest tests against real SQL/emulator resources, and the failure/recovery demo from a hash-checked fresh copy of tracked and intended untracked inputs. It uses unique Compose names, random loopback ports and automatic owned-volume/secret cleanup. Reports are under `artifacts/verification` and `artifacts/test-results`. No commit or push is required.

Focused development check:

```powershell
# Only needed for direct Testcontainers invocation on Docker Desktop/Windows:
$env:DOCKER_HOST = 'npipe://./pipe/dockerDesktopLinuxEngine'
dotnet test .\tests\RelayLab.Tests\RelayLab.Tests.csproj -c Release --no-restore `
  --filter 'FullyQualifiedName~RetryTests|FullyQualifiedName~RecoveryTests'
Remove-Item Env:DOCKER_HOST
```

The verification script manages the Docker context itself. The GitHub Actions workflow invokes the same M2 acceptance suite and recovery demo on Ubuntu 24.04. See [STATUS](docs/STATUS.md) for actual local results, tested commits and remote CI evidence; historical M1 runs do not establish M2 verification.

## Stop and troubleshoot

```powershell
pwsh -NoProfile -File .\eng\demo.ps1 -Action Down
# Explicitly delete this demo project's SQL volume and generated .env:
pwsh -NoProfile -File .\eng\demo.ps1 -Action Reset
```

For a custom `-ProjectName`, pass the same name to Down/Reset. Saved ports are reused. `Down` keeps M2 SQL/receiver state but the emulator loses its queue and Aspire loses diagnostics.

| Symptom | Action |
| --- | --- |
| Docker unavailable | Start Docker Desktop's Linux engine. |
| Persistence/readiness 503 | Check SQL/init logs; resolve connectivity/schema and retry the same key. |
| Pending or expired Processing | Inspect worker, next eligibility and DLQ; SQL recovery needs a running worker and SQL/broker connectivity. |
| Failed / Unknown | Inspect the full history; the receiver may have an effect. Replay deliberately with one stable replay key. |
| Port collision | On a fresh demo, set `-ApiPort`, `-ReceiverPort`, `-BrokerHealthPort`, `-DashboardPort`. |
| Missing traces after a kill | Export buffers are not durable. Inspect SQL; rerun the demo, which waits for trace ingestion before the kill. |

Architecture, decisions, acceptance and operations live in [ARCHITECTURE](ARCHITECTURE.md), [DECISIONS](docs/DECISIONS.md), [TESTING](docs/TESTING.md), [OPERATIONS](docs/OPERATIONS.md) and [PLAN](PLAN.md). M3 cloud identity, migrations, deployment and rollback remain future work. The existing [license](LICENSE) is retained.
