# CI/CD and operations

## M1: reproducible local execution and CI

Create an eng/ verification entry point after the actual solution and runner exist. It must propagate failures, restore locked dependencies, build, run required tests and provide useful logs. Generated output belongs under artifacts/, bin/obj or TestResults and remains ignored.

Author one GitHub Actions workflow for a Linux Docker-capable runner. Use tested toolchain/container versions, scoped workflow permissions and no deployment secrets for ordinary PR tests. Retain diagnostics when tests fail. Verify third-party action references from their official repositories and pin them appropriately. Do not label authored YAML as executed CI.

Align Compose and tests on images, queue configuration and application prerequisites. Health checks and startup readiness must reflect required dependencies. Supply a local .env.example with placeholders and documented generation/setup of local secrets. Bind host demo ports to loopback. Avoid committing credentials or storing dependency image copies in Git.

Implemented entry points: `eng/verify.ps1` and `eng/demo.ps1`; versions live in `infra/versions.json`. Compose checks SQL with sqlcmd; the demo polls actual API/receiver database readiness and emulator `/health` before starting the worker. Explicit local initialization services run `--init-db` once before the application services start. Do not bypass these readiness steps on the first M1 delivery, which has no scheduled HTTP retry.

The authored `.github/workflows/ci.yml` uses an Ubuntu 24.04 Docker-capable runner, the selected SDK, read-only repository permissions and commit-pinned official actions. It invokes the same verification script, retaining selected TRX/log diagnostics for seven days. It does not upload `.env` or complete fresh candidate directories. Remote execution is recorded only in STATUS when it actually occurs.

## M2: diagnostic operations

Export OpenTelemetry traces across acceptance, outbox publishing, message consumption and outbound HTTP. Record counters/histograms for pending work, attempts, failures and latency. Do not use delivery IDs as metric labels; they belong in trace/log context. Test propagation rather than assuming ambient HTTP context crosses a queue automatically.

Keep payloads, connection strings and signing secrets out of telemetry. Durable SQL history must remain useful without the telemetry collector. Record a small runbook for backlog, receiver failure, stale claim and terminal failure/replay.

## M3: prepare before provisioning

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
