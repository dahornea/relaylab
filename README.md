# RelayLab

A locally reproducible .NET 10 webhook delivery project: SQL acceptance and outbox persistence, durable retries, idempotent replay, worker recovery and OpenTelemetry. It runs with SQL Server, the Service Bus emulator and Aspire in Docker. **No Azure account, subscription, CLI or credentials are needed to clone, build, test or run the demo.**

The API and worker share delivery state. A separate receiver commits its receipt and effect together using the permanent delivery ID. HTTP can repeat; this cooperating receiver demonstrates one effect per delivery in the tested scenarios. This is a reliability demonstration, not a production-readiness or universal exactly-once claim.

## Run locally

Prerequisites: Git, **PowerShell 7**, **.NET SDK 10.0.400** (or a compatible patch in that feature band), and Docker with a **Linux-container engine** and Compose. Allow at least 6 GB for SQL, Service Bus and Aspire. Initial restore/build downloads public NuGet packages and pinned Microsoft images. SQL Developer/emulator license acceptance is configured for local development.

From PowerShell 7:

```powershell
git clone https://github.com/dahornea/relaylab.git
Set-Location relaylab
pwsh -NoProfile -File ./eng/demo.ps1
```

The script generates an ignored random local SQL password, builds all three applications and explicitly initializes fresh databases. It checks normal acceptance (`202`), identical repeat (`200`), Delivered status and one effect, then demonstrates:

1. Three HTTP 503 attempts exhaust the generation.
2. Replay returns 202; repeating its key returns 200 with the same work identity.
3. The receiver commits an effect while withholding acknowledgement. The demo kills the actual worker with SIGKILL while SQL still records Started.
4. Worker and receiver restart against retained SQL. Recovery finishes with five attempts, an Interrupted/Unknown outcome and one receiver effect.

Evidence is saved under `artifacts/demo/`. Use `-Scenario Happy` for the ordinary delivery flow alone.

| Endpoint | Local address |
| --- | --- |
| API | http://127.0.0.1:5080 |
| Receiver | http://127.0.0.1:5081 |
| Aspire | http://127.0.0.1:18888 |

Ports bind to loopback; SQL, AMQP and OTLP stay inside Compose. In Aspire **Traces**, filter on `@relaylab.delivery.id:<deliveryId>` from `recovery-result.json`. **Metrics → Table** shows worker attempts, outcomes, pending work and HTTP duration. Telemetry is buffered and can be lost; SQL history remains authoritative.

```powershell
$result = Get-Content -Raw artifacts/demo/recovery-result.json | ConvertFrom-Json
Invoke-RestMethod "http://127.0.0.1:5080/deliveries/$($result.deliveryId)?after=0&limit=50" | ConvertTo-Json -Depth 8
Invoke-RestMethod "http://127.0.0.1:5081/receipts/$($result.deliveryId)"
pwsh -NoProfile -File ./eng/demo.ps1 -Action DeadLetters
```

## Build and verify

From the checkout in PowerShell 7:

```powershell
dotnet restore RelayLab.slnx --locked-mode --configfile NuGet.Config
dotnet build RelayLab.slnx -c Release --no-restore
# Complete verification, including real SQL/broker tests and a fresh recovery demo:
pwsh -NoProfile -File ./eng/verify.ps1
```

Verification runs all **63 tests** with xUnit/VSTest, uses isolated disposable databases and random loopback ports, copies/hash-checks candidate inputs, builds Linux application containers, and cleans up its own resources. Reports are under `artifacts/verification` and `artifacts/test-results`. Docker context handling is automatic; no manual connection strings or Terraform installation are needed for application verification.

Ordinary [GitHub CI](.github/workflows/ci.yml) additionally validates Terraform and workflows, then runs the same unfiltered tests and recovery demo on Ubuntu 24.04. Terraform initialization disables the backend; CI never logs in to Azure. [STATUS](docs/STATUS.md) identifies the actual tested commits/runs and distinguishes local evidence from remote results.

## Schema, cleanup and reliability limits

**Use a fresh local M2 schema. Existing M1 databases are not upgraded; initialization rejects them without erasing data.** Preserve old databases in their own checkout/project. A fresh clone/demo or isolated verification creates the required schema; process restarts retain the receiver's SQL deduplication ledger. Do not reuse an old database by treating reset as migration.

```powershell
# Stop the default demo while retaining its SQL volume:
pwsh -NoProfile -File ./eng/demo.ps1 -Action Down
# Delete only this disposable demo project's volume and generated .env:
pwsh -NoProfile -File ./eng/demo.ps1 -Action Reset
```

If you used `-ProjectName`, pass the same name to Down/Reset. Saved ports are reused; set `-ApiPort`, `-ReceiverPort`, `-BrokerHealthPort` and `-DashboardPort` when starting a fresh demo to avoid collisions.

SQL retains accepted intent and persisted retry eligibility. Competing workers use expiring, fenced leases; a stale owner cannot overwrite newer state. Retry/replay preserves the logical delivery ID while work items and attempts have separate identities. The default budget is three attempts per generation, including interruptions. Retry timeouts, transport failures, 408/429 and 5xx with bounded backoff; other failures stop the generation. Replay is explicit and idempotent.

`Failed` does not prove that the receiver had no effect. HTTP runs outside SQL transactions, so a committed effect and lost response remain ambiguous. Progress requires retained SQL, available dependencies and a recoverable receiver. The **Service Bus emulator loses messages on restart**; SQL reconciliation and local tests do not prove Azure-hosted broker durability. Aspire is also in-memory. See [contracts](docs/CONTRACTS.md), [testing](docs/TESTING.md) and [operations](docs/OPERATIONS.md) for details.

## Optional cloud preparation — deferred

The owner has chosen **not to create an Azure subscription or deploy**. Useful M3 preparation is retained: Terraform, scoped identity/OIDC configuration, authenticated ingress/receiver code, explicit schema jobs, immutable-image deployment, smoke/recovery checks and rollback/teardown procedures. [CLOUD.md](docs/CLOUD.md) records this locally validated preparation and its limitations.

The cloud workflow has **only a manual trigger**. Pushes, pull requests, schedules and ordinary CI cannot invoke it. No cloud workflow is dispatched during verification. Azure deployment, effective permissions, billing, cloud rollback and teardown remain unexecuted; original M3 cloud acceptance is incomplete. Cloud schema initialization requires new empty databases and rejects unversioned M1/M2 databases; rollback only supports compatible verified M3 images.

To reproduce the additional static checks, install checksum-verified Terraform **1.16.1** on PATH, then run `./eng/install-actionlint.ps1` followed by `./eng/verify-m3.ps1 -StaticOnly` in the same PowerShell session. These checks need internet downloads, but no Azure login. The full `eng/verify-m3.ps1` also runs application verification.

[Architecture](ARCHITECTURE.md) · [Decisions](docs/DECISIONS.md) · [Original plan](PLAN.md) · [License](LICENSE)
