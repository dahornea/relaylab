# Current status

## Current task

- State: **M1 complete locally**. Required verification passed and independent review found no concrete blocker.
- Authorized scope: owner's 2026-09-05 M1 request and subsequent finalization request authorize normal M1 commits, push to the intended GitHub repository and actual CI verification. M2/M3, releases, package publication, access-setting changes and cloud provisioning remain unauthorized.
- Implemented: API validation/acceptance/status, SQL transactional outbox, Service Bus publisher/consumer, recoverable claims and durable attempts, separate receiver ledger/effect, Docker demo, eng verification and authored CI.
- Existing license and bootstrap context retained. Publication candidate includes the reviewed application, tests, scripts, CI, lock files and bootstrap guidance; generated artifacts and local secrets are excluded.

## Environment and versions

- Windows 11 (10.0.26200), .NET SDK 10.0.400, .NET/ASP.NET runtime 10.0.11.
- PowerShell 7.6.5, Docker Desktop 4.89.0, Compose 5.5.0, Linux amd64 Engine 29.7.2. Initial engine-unavailable probe failed; owner started Docker, then actual connectivity passed.
- EF Core SQL Server 10.0.11; Azure.Messaging.ServiceBus 7.20.2; Testcontainers.MsSql/ServiceBus 4.14.0.
- Runner: VSTest, Microsoft.NET.Test.Sdk 18.9.0, xUnit 2.9.3, xunit.runner.visualstudio 3.1.5. All projects have package lock files.
- Images: SQL `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`; Service Bus `mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0`; application SDK `mcr.microsoft.com/dotnet/sdk:10.0.400`, runtime `mcr.microsoft.com/dotnet/aspnet:10.0.11`. Shared inventory: infra/versions.json.
- Project config requests gpt-6-astra. Exact parent-task model is not exposed by the client. Configured read-only researcher, test_designer and reviewer ran successfully; no model/config changes. Only the primary agent wrote files and ran builds/tests.

## Verification evidence

- Initial restore generated locks; subsequent `dotnet restore RelayLab.slnx --locked-mode --configfile NuGet.Config` passed.
- `dotnet build RelayLab.slnx --no-restore -c Release`: passed, zero warnings/errors.
- Real SQL and broker connectivity probe: passed (1 test), artifacts/connectivity/connectivity.trx.
- Focused AcceptanceTests: passed (9 cases), artifacts/development/acceptance-development.trx.
- Focused FailureTests and PolicyTests: passed (11 cases), artifacts/development/failures-development.trx.
- Full verification: `& ./eng/verify.ps1` executed in PowerShell 7.6.5, exit 0. Copy-ready equivalent: `pwsh -NoProfile -File ./eng/verify.ps1`.
- Final suite: **21 passed, 0 failed, 0 skipped** (15 SQL/broker-backed cases and 6 pure validation cases). Evidence: artifacts/test-results/m1.trx and artifacts/verification/{restore,build,tests}.log.
- Fresh-input container demo: **passed**, delivery a4c33a52-2e8a-4d70-bb30-a457a436b948, Delivered, one attempt, one receiver effect, identical repeat 200. Inputs included tracked and intended untracked files, with SHA-256 comparisons. Evidence: artifacts/verification/candidate-inputs.sha256, fresh-demo.log and fresh-demo-result.json. Isolated Compose containers/network/SQL volume and generated .env were cleaned up; build images/cache remain local.
- Verification-script defects fixed during execution: normalize Docker Desktop's Windows pipe URI for Testcontainers only, then restore Docker CLI's environment before Compose. Earlier failed runs are not counted as passes.
- Post-review focused check: `dotnet test tests/RelayLab.Tests/RelayLab.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Outbox_insert_failure --logger 'trx;LogFileName=atomicity-review.trx' --results-directory artifacts/review` passed (1/1). A test-only interceptor now asserts SQL error 51000, proving the trigger observed the already-written Delivery before rollback. Production source is unchanged from the full 21-case/fresh-demo pass; this affected test was rebuilt and rerun after its stronger assertion.
- Remote CI: NOT RUN; `.github/workflows/ci.yml` authored with verified official action commit references.
- Azure deployment: NOT RUN; M3 requires its own request and resource approval.
- Linux containers executed SQL, broker and all three published applications. The xUnit test host ran on Windows; Linux-host xUnit execution is UNVERIFIED.

## Review and remaining limits

- Read-only reviewer inspected source, intended untracked files, contracts, CI/scripts, TRX, build and demo logs. Verdict: no concrete M1 blocker; bounded review judgment, not proof of correctness.
- Its optional atomicity-oracle improvement was implemented and the affected real SQL check passed. No unresolved review finding remains.
- Local verification checks confirmed the existing LICENSE blob is unchanged, generated `.env`/artifacts are ignored, and no isolated verification containers or SQL volumes remain. Finalization now authorizes normal commit/push and CI execution; no access settings or cloud resources are changed.
- M1 has one normal HTTP attempt, recoverable interrupted claims and outbox transport retries. Scheduled retries/replay, automatic dead-letter reconciliation, competing-worker crash matrices and telemetry are M2 work. Published work can remain incomplete after emulator restart or broker exhaustion; inspect status warnings and preserved dead letters. Receiver cooperation is necessary for idempotent effects.
- Local schema initialization uses EnsureCreated and is not a schema-upgrade mechanism. SQL CU14 is the tested local compatibility baseline, not a current production security recommendation. Authentication, identity, migrations, cloud operations and Azure acceptance remain M3 work.

## Next step

Commit and push the reviewed M1 candidate to dahornea/relaylab on main, inspect its actual GitHub Actions logs/artifacts, and record the tested implementation commit and remote results in a separate documentation update. Reuse the valid local evidence above; run additional checks only for concrete changes or failures. M2/M3 require a separate request.

## Update convention

Replace this current record as work progresses. Record milestone/authorization reference, meaningful completed work, selected environment/versions, exact tested commands, evidence location, unresolved risks and the next action. Distinguish local, remote CI and Azure evidence. Do not append full tool logs or duplicate the entire plan. A status note cannot create new owner authorization.
