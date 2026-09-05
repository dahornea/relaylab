# Current status

## M2 finalization

- **M2 passed complete local verification on 2026-09-05.** The owner authorizes a normal implementation commit/push to dahornea/relaylab and remote CI verification. The implementation candidate is ready; remote M2 CI evidence is pending at this record's revision.
- Started from `main` at `434ee1283eee887f7932133d0b786a28af175c30`, matching `origin/main`. The intended remote is https://github.com/dahornea/relaylab. The 47 intended changed/new files contain M2 implementation, tests, local infrastructure, workflow and documentation; no unrelated changes were present.
- Implemented persisted bounded retries and scheduling, exhaustion, idempotent replay, lease recovery, SQL reconciliation, competing-worker fencing and OpenTelemetry. Logical delivery identity survives retry/replay; each work slot and Started attempt has its own identity. HTTP runs outside SQL transactions. Interrupted remote outcomes remain Unknown.
- No M3 work, Azure resources, migrations, frontend, release, package publication, force-push, history rewrite or access-setting changes are included.

## Required schema and setup

**M2 requires a fresh local schema. It cannot upgrade an M1 database.** Initialization rejects the old schema without erasing it. Preserve existing M1 data in its own database/project; do not reset a user database to verify M2. The verification script creates new Testcontainers and a uniquely named Compose project with disposable databases/volumes. M2 process restarts retain its SQL state and the receiver's durable deduplication ledger.

Use PowerShell 7 from your RelayLab checkout:

```powershell
pwsh -NoProfile -File eng/demo.ps1
pwsh -NoProfile -File eng/verify.ps1
```

README explains prerequisites, isolation, replay, metrics and explicit cleanup. Verification never reuses or resets an existing user database.

## Complete final local verification

- Executed `pwsh -NoProfile -File eng/verify.ps1` against the final implementation: **exit 0**. Locked NuGet restore passed; Release build passed with **zero warnings/errors**; **47/47 tests passed, zero failed/skipped**. TRX class/case counts were compared with all current Fact/InlineData cases: nothing missing.
- **30 real SQL/broker integration cases:** Acceptance 9, Connectivity 1, Failure 5, Retry 4, Recovery 8, BrokerRecovery 2, Telemetry 1. **17 pure cases:** Policy 6, RetryPolicy 11, including HTTP 599/600 boundaries. All ran together. This closes the earlier 45-case full-run plus 12-case focused-run gap.
- Integration tests use actual SQL Server, Service Bus emulator, SDK and Kestrel hosts. They cover persisted eligibility, repeated/competing replay, rollback at durable transitions, stale owners, interrupted claims/HTTP/completion/publication, broker exhaustion, connectivity interruption retaining broker state, and an unavailable telemetry exporter.
- Local host: Windows 11, SDK 10.0.400 / .NET and ASP.NET 10.0.11, PowerShell 7.6.5; Docker Desktop 4.89.0, Linux amd64 Engine 29.7.2, Compose 5.5.0. Host xUnit ran on Windows; fresh demo applications and dependencies ran in Linux containers.
- Logs: artifacts/m2-finalization/full-local.log and artifacts/verification/{restore,build,tests,fresh-demo,fresh-demo-cleanup}.log. TRX: artifacts/test-results/m2.trx. Expected case inventory: artifacts/m2-finalization/expected-tests.json. Previous evidence is preserved under artifacts/m2-finalization/previous.
- The fresh demo copied and hash-checked tracked plus intended untracked inputs; manifest: artifacts/verification/candidate-inputs.sha256. All current implementation/test/workflow files match it. Only this evidence document was updated after execution.

## Fresh-input recovery and telemetry

- Isolated Compose project `relaylab-verify-6946a9edee` used fresh databases. Ordinary delivery `c80bf4bc-d1f4-4dda-9d96-4eee8aa66f72` reached Delivered with one attempt/effect and repeated submission 200.
- Recovery delivery `3ba6e417-5c2b-470e-bf7c-8c64694a0485`: three HTTP 503 attempts ended RetryExhausted; replay returned 202, then 200 with the same work identity. Actual worker SIGKILL followed a committed receiver effect while SQL still recorded Processing/Started. Receiver and worker restarted against retained SQL. Final state: **Delivered, generation 1, five attempts, one Interrupted/Unknown outcome, one durable receiver effect**.
- Aspire ingested trace `9410702341c46d915e2fe77b421a9cf2`: **24 spans**, all eight required lifecycle span kinds, API/worker/receiver services and one correlated trace identity. artifacts/verification/fresh-recovery-{result,trace}.json and {exhausted,interrupted,recovered}.json retain the observations.
- Numeric metric evidence is reused from the unchanged implementation's earlier browser check: worker HTTP duration Table displayed P50/P90/P99 = 0.25 seconds (bucket estimates); outcome samples and started/pending/publication instruments were inspected. artifacts/review/metrics-ui.md records that actual Aspire observation. Metric export was not inferred from the trace API.
- Owned test/demo resources and generated .env were cleaned up. Existing unrelated MongoDB/Redis containers and their data were preserved. Logs, reports, fresh source copies, downloaded images and build caches remain local/ignored.

## Review and commit hygiene

- Reused the completed configured read-only test_designer and final reviewer reviews, including the focused retry-range/histogram follow-up: **no outstanding concrete findings**. No implementation/test/workflow changes were needed during finalization, so another code review was not necessary. The primary agent performed all execution and documentation edits.
- Reviewed the complete prospective diff including all 13 new untracked source/test/script files. Generated secrets, credentials, private machine paths, build output, caches and test artifacts are excluded. README setup commands are checkout-relative. Intentional synthetic SQL failures, sentinel values and the emulator SAS placeholder remain.
- `git diff --check` passed. AGENTS, role configuration and LICENSE are unchanged. No assertions were weakened, skipped, filtered out of full acceptance or replaced by mocks.

## Dependency pins

- EF Core SQL Server 10.0.11; Azure.Messaging.ServiceBus 7.20.2; Testcontainers SQL/Service Bus 4.14.0; xUnit 2.9.3, adapter 3.1.5, VSTest Microsoft.NET.Test.Sdk 18.9.0; OpenTelemetry hosting/OTLP 1.18.0. Updated lock files passed locked restore.
- infra/versions.json pins SQL Server 2022-CU14-ubuntu-22.04, Service Bus emulator 2.0.0, SDK 10.0.400, ASP.NET 10.0.11 and Aspire dashboard 13.5.2.

## Remote CI and remaining limitations

- **M2 remote CI / Linux-hosted xUnit: pending.** The existing Ubuntu 24.04 workflow invokes the unfiltered complete M2 verification entry point, including real dependencies and the fresh-input recovery demo; evidence will be recorded after the authorized push and actual run.
- Historical M1 implementation `3b4e498` passed [run 33975051554](https://github.com/dahornea/relaylab/actions/runs/33975051554); later documentation `434ee12` passed [run 33975506539](https://github.com/dahornea/relaylab/actions/runs/33975506539). These are not M2 evidence.
- Azure behavior is **NOT RUN**. Emulator execution does not establish Azure durability or identity. Progress requires retained SQL and available dependencies; permanent recipient failure can prevent completion. Valid dead letters remain inspectable while SQL regenerates current signals.
- There is no universal exactly-once guarantee: fencing cannot undo an external effect, and a receiver can commit before sender acknowledgement. The sample's one effect relies on its durable receipt/effect transaction.
- Telemetry is buffered/lossy and Aspire is in-memory. SQL history has no archival policy. Production authentication/signing, controlled migrations and cloud operations remain outside M2.
