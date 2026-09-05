# RelayLab

A .NET webhook delivery service focused on durable messaging, idempotent processing, and failure recovery with SQL Server and Azure Service Bus.

M1 implements local durable acceptance, an SQL outbox, Service Bus delivery, a status API and an idempotent sample receiver. A non-2xx response or timeout ends the single normal HTTP attempt as `Failed`. Scheduled HTTP retries, replay and telemetry belong to M2. Azure deployment belongs to M3.

## Run the local demo

Prerequisites: .NET SDK **10.0.400** (or a compatible patch), **PowerShell 7**, Git, and Docker Desktop running **Linux containers** with enough memory for SQL Server and the Service Bus emulator (allow at least 6 GB). The first run downloads Microsoft container images and NuGet packages. The emulator and SQL Developer images are for local development; their local license acceptance is configured in Compose/Testcontainers.

From PowerShell 7, in this repository:

```powershell
Set-Location D:\relaylab
pwsh -NoProfile -File .\eng\demo.ps1
```

The script generates an ignored `.env` with a random local SQL password, builds the API/Worker/Receiver images, initializes two separate application databases, waits for dependencies, and submits an event twice. It checks `202`, an idempotent `200`, eventual `Delivered`, and one receiver effect. It prints the actual delivery ID, status and effect count, and saves `artifacts/demo/result.json`.

API: `http://127.0.0.1:5080`. Sample receiver: `http://127.0.0.1:5081`. The emulator health endpoint is published on loopback port 5300; SQL and AMQP stay inside the Compose network. All application executables require an explicit Development/Testing environment; M1 does not support public authenticated ingress.

Submit and inspect another event:

```powershell
$body = '{"destinationId":"demo","eventType":"document.ready","data":{"documentId":"doc-002"}}'
$delivery = Invoke-RestMethod http://127.0.0.1:5080/events -Method Post `
  -ContentType application/json -Headers @{ 'Idempotency-Key' = 'example-002' } -Body $body
Invoke-RestMethod "http://127.0.0.1:5080/deliveries/$($delivery.deliveryId)" | ConvertTo-Json -Depth 6
Invoke-RestMethod "http://127.0.0.1:5081/receipts/$($delivery.deliveryId)"
```

Stop the demo while retaining SQL state, or explicitly delete its local data:

```powershell
pwsh -NoProfile -File .\eng\demo.ps1 -Action Down
# Deletes this Compose project's SQL volume and generated .env:
pwsh -NoProfile -File .\eng\demo.ps1 -Action Reset
```

The emulator loses its messages on restart. `Down` retains application/receiver SQL but cannot preserve the emulator queue. Use a fresh event after restart; do not use an emulator restart as a broker durability demonstration. For M1 backlog demonstration, stop only the worker, submit an event, then resume the worker:

```powershell
docker compose stop worker
# Submit with a new key using the request above; observe Pending.
docker compose start worker
```

## Verify M1

```powershell
Set-Location D:\relaylab
pwsh -NoProfile -File .\eng\verify.ps1
```

This is the full verification entry point: locked restore, Release build with warnings as errors, all xUnit/VSTest tests using owned Testcontainers SQL/emulator resources, then a container demo built from a hash-checked fresh copy of tracked **and intended untracked** inputs. The fresh demo uses a unique Compose project, random loopback ports, generated secrets and automatic owned-volume cleanup. Reports are under `artifacts/verification` and `artifacts/test-results`. No commit or push is required.

For a focused development run after restore:

```powershell
dotnet test .\tests\RelayLab.Tests\RelayLab.Tests.csproj -c Release --no-restore `
  --filter FullyQualifiedName~AcceptanceTests
```

The verification script selects the active Docker context for Testcontainers. If running `dotnet test` directly on Windows requires an explicit endpoint, use `$env:DOCKER_HOST = 'npipe://./pipe/dockerDesktopLinuxEngine'` for Docker Desktop's Linux engine.

See [current execution evidence](docs/STATUS.md) for actual checks. [GitHub Actions run 33975051554](https://github.com/dahornea/relaylab/actions/runs/33975051554) passed on Ubuntu 24.04.4 for commit `3b4e498`: locked restore, warning-clean Release build, all 21 tests (including 15 SQL/broker-backed cases), and the fresh-input container demo with cleanup. Azure deployment has not been run.

## Delivery boundaries

- A successful new POST follows one committed SQL transaction containing both delivery and outbox. A unique, case-sensitive key resolves concurrent requests. Reusing it with conflicting typed values returns `409` and code `idempotency_conflict`.
- Publication can repeat across the SQL/broker boundary. Each outbox item retains its message ID. The receiver commits a unique delivery receipt and its effect in one transaction, including across receiver restart.
- SQL leases expire; only the current unexpired owner can persist a delivery result. HTTP takes place outside SQL transactions. A started attempt survives interruption, and a terminal result is stored before message completion.
- `Failed` does not prove the receiver had no effect. Timeouts/transport failures report `Unknown`. There is no M1 HTTP retry scheduler or replay endpoint. Broker redelivery can recover interrupted work and may repeat HTTP.
- Status includes publication information, bounded attempt history, expired-lease warnings and honest incomplete-state warnings. The dead-letter queue is preserved and inspected explicitly; automatic reconciliation is deferred to M2.

```powershell
pwsh -NoProfile -File .\eng\demo.ps1 -Action DeadLetters
docker compose logs --tail 100 worker
```

Dead-letter inspection peeks at the first 50 messages, prints safe work IDs/reason categories, and removes nothing. Match the work ID to `OutboxMessages.Id` when diagnosing locally. A broker dead letter is distinct from an application's `Failed` outcome.

| Symptom | Action |
| --- | --- |
| Docker endpoint unavailable | Start Docker Desktop and wait for the Linux engine; run verification again. |
| `persistence_unavailable` / readiness 503 | Check `docker compose logs sql init-api init-receiver`; retry the same key after SQL recovers. |
| Pending or expired Processing | Inspect worker logs, publication status and the dead-letter queue. M1 has no replay operation. |
| Failed / Timeout / Unknown | Inspect the attempt. The receiver may already have committed its effect; do not infer otherwise. |
| Local port already in use | Stop the conflicting process or use the script's `-ApiPort`, `-ReceiverPort`, `-BrokerHealthPort` arguments on a newly initialized demo. |

## Project documents

- [Product and scope](PRODUCT.md)
- [Architecture](ARCHITECTURE.md) and [contracts](docs/CONTRACTS.md)
- [Milestones and acceptance](PLAN.md)
- [Experience and presentation design](docs/DESIGN.md)
- [Testing strategy](docs/TESTING.md)
- [CI/CD and operations](docs/OPERATIONS.md)
- [Review criteria](CODE_REVIEW.md)
- [Current progress](docs/STATUS.md)
- [Sources and dependency selection](docs/SOURCES.md)

The service permits repeated HTTP delivery. A cooperating sample recipient demonstrates idempotent effects; this is not a universal exactly-once guarantee. The existing [license](LICENSE) is retained.
