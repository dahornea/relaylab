# CI/CD and operations

## Current scope: local execution and GitHub CI

The owner deferred Azure deployment and will not create a subscription. Clone, build, all tests and Docker demos need only the local prerequisites in README; no Azure credentials or CLI are needed. Keep the M3 files as optional preparation. Effective cloud permissions, deployment, billing, cloud rollback and teardown remain unexecuted, and the original M3 cloud acceptance is incomplete.

The inspected workflow chain is `ci.yml` → `eng/verify-m3.ps1` → `eng/verify.ps1` → `eng/demo.ps1`. CI runs on push to main, pull request or manual dispatch, with read-only repository permission. Its Terraform commands use `init -backend=false` and `validate`; it parses cloud scripts but does not execute them. The only trigger on `cloud.yml` is `workflow_dispatch`; it has no push, pull-request, schedule, workflow_run or workflow_call trigger. No ordinary workflow/script dispatches cloud deployment. Configuration is validated statically, never by dispatching the cloud workflow. Actual GitHub CI evidence belongs in STATUS.

## M1: reproducible local execution and CI

Create an eng/ verification entry point after the actual solution and runner exist. It must propagate failures, restore locked dependencies, build, run required tests and provide useful logs. Generated output belongs under artifacts/, bin/obj or TestResults and remains ignored.

Author one GitHub Actions workflow for a Linux Docker-capable runner. Use tested toolchain/container versions, scoped workflow permissions and no deployment secrets for ordinary PR tests. Retain diagnostics when tests fail. Verify third-party action references from their official repositories and pin them appropriately. Do not label authored YAML as executed CI.

Align Compose and tests on images, queue configuration and application prerequisites. Health checks and startup readiness must reflect required dependencies. Supply a local .env.example with placeholders and documented generation/setup of local secrets. Bind host demo ports to loopback. Avoid committing credentials or storing dependency image copies in Git.

Implemented local entry points: `eng/verify.ps1` and `eng/demo.ps1`; versions live in `infra/versions.json`. Compose checks SQL with sqlcmd; the demo polls API/receiver database readiness, emulator `/health` and the dashboard before starting work. Explicit local initialization services run `--init-db` before application startup. M2 initialization requires a fresh M2 schema and rejects the older M1 schema; it never silently upgrades or erases data. M3 adds a separate explicit fresh versioned baseline; it does not migrate existing M1/M2 data.

The authored `.github/workflows/ci.yml` uses an Ubuntu 24.04 Docker-capable runner, the selected SDK, read-only repository permissions and commit-pinned official actions. It invokes the same verification script, retaining selected TRX/log diagnostics for seven days. It does not upload `.env` or complete fresh candidate directories. Remote execution is recorded only in STATUS when it actually occurs.

## M2: diagnostic operations

Export OpenTelemetry traces across acceptance, outbox publishing, message consumption and outbound HTTP. Record counters/histograms for pending work, attempts, failures and latency. Do not use delivery IDs as metric labels; they belong in trace/log context. Test propagation rather than assuming ambient HTTP context crosses a queue automatically.

Keep payloads, connection strings and signing secrets out of telemetry. Durable SQL history must remain useful without the telemetry collector. Record a small runbook for backlog, receiver failure, stale claim and terminal failure/replay.

Implemented viewer: standalone Aspire 13.5.2, frontend port 18888 published to loopback with anonymous local access. OTLP gRPC on container port 18889 is internal to Compose. API, worker and receiver export manual lifecycle spans and metrics via OpenTelemetry 1.18.0. No additional collector, custom frontend or external telemetry account is needed. The trace parent is stored with the accepted delivery and used explicitly across outbox, retry and replay; receiver HTTP context propagates separately.

Worker instruments: `relaylab.pending` (SQL count of Pending, including scheduled future work), `relaylab.attempt.started`, `relaylab.attempt.outcomes` with bounded outcome tags, `relaylab.publications` with sent/unavailable tags, and `relaylab.http.duration` in seconds. API exposes accepted/replays counters; receiver exposes newly committed effects. Pending is a per-worker observation of shared SQL, not additive across replicas. Counters/spans may be lost on a crash, and an Interrupted transition records uncertainty rather than inventing an HTTP end time.

The HTTP histogram uses explicit second-valued boundaries from 5 ms through 20 seconds. Viewer percentiles are bucket estimates, not exact individual durations; use the webhook span for an individual request duration.

Local runbook:

| Condition | Inspect and act |
| --- | --- |
| Backlog | GET status shows current work, publication attempts and next eligibility. Restore SQL/broker connectivity and a running worker; its one-second scheduler republishes current Pending signals periodically. |
| Receiver failure | Inspect attempt status/category. Resolve the configured destination; transient categories retry within the persisted budget. Nonretryable responses end the generation. |
| Expired claim | Status warns of unknown remote outcome. Recovery consumes the interrupted slot and schedules another or exhausts. Do not rewrite SQL in normal operation; deterministic SQL-time changes belong only to the tests. |
| Terminal Failed | Inspect history and the possibility of an existing receiver effect. POST replay with a new stable key; repeat that key after a lost response. |
| Broker DLQ | Run demo `-Action DeadLetters` to peek 50 safe IDs/reasons. SQL handles current Pending intent independently; malformed/unknown signals require investigation and are not silently removed. |
| Viewer outage | Delivery/status continue. Restore Aspire to collect new diagnostics; lost buffered data is not reconstructed from SQL. |

The sample receiver alone has startup modes Acknowledge, Reject, CommitThenAbortOnce and CommitThenWaitOnce. The latter two apply only to a newly inserted durable receipt; duplicate receipts acknowledge normally even after process restart. The demo sets these through Compose environment and recreates the sample. No fault controls are exposed through production ingress.

## M3: prepared operations

The concrete implementation and exact commands are in [CLOUD.md](CLOUD.md): inventory/cost, owner/bootstrap permissions, remote state, GitHub environment/OIDC, runtime table/queue permissions, explicit schema jobs, immutable rollout, smoke/failure/telemetry verification, application rollback and two-stage teardown. Current execution evidence is in STATUS; this preparation does not establish M3 cloud acceptance.

`eng/verify-m3.ps1` is the repository verification entry point including optional infrastructure preparation. `ci.yml` runs backend-disabled Terraform validation and actionlint, then the entire real SQL/broker/container verification. `cloud.yml` remains manual-only and deferred; its authored main/environment/exact-SHA CI gates do not establish that any Azure identity or GitHub environment has been configured. No cloud workflow is executed during local finalization.

`--deploy-schema` creates a locked, versioned fresh baseline and grants runtime DML permissions. Production rejects `--init-db` and verifies baseline compatibility on startup. Existing M1/M2 databases are not adopted/upgraded/reset. Compatible old M3 images can be redeployed as new Single-mode revisions after the target schema job verifies the unchanged baseline. The scripts never perform an automatic database downgrade.

The following original operating requirements are retained for a future separately authorized cloud effort; they are not current execution tasks:

Target one Azure environment using Container Apps, Azure SQL and Service Bus. Add registry/logging/identity resources only as required by the selected deployment. Terraform manages the reviewed resource set; protect state and secrets outside Git and track the provider lock file.

Prepare the following before requesting paid-resource approval:
- Region, SKU/resource inventory and current estimated cost for the intended test duration.
- Required subscription/tenant access and role assignments.
- GitHub OIDC federation and application managed identities with scoped access.
- Authenticated API ingress and controlled recipient access/signing policy.
- Migration execution, readiness, smoke checks and application revision rollback.
- An explicit teardown command targeting only the project-owned resources.

Do not assume an Azure subscription, paid budget or permission to edit GitHub settings. A missing account does not block authoring and local validation of the configuration; label what remains unexecuted.

## Deploy, verify, roll back

Build each executable image once per candidate, record its immutable identity and deploy that image. Run migrations as a controlled step rather than racing application replicas on startup. Choose schema changes compatible with the demonstrated rollback or state the incompatibility before deployment.

Execute CI/CD, cloud smoke and one failure/recovery case. Verify the identity/access configuration in Azure. Roll back an application revision and confirm behavior; do not call a destructive database downgrade a safe automatic rollback. Retain redacted commit, pipeline, environment and image evidence.

Remove the temporary environment after the authorized demonstration unless the owner approves retaining it. Verify teardown against the actual resource inventory. Do not claim cloud verification from files, a plan or an emulator.
