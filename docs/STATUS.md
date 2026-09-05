# Current status

## Current task

- State: **M1 complete, committed and pushed; Linux GitHub Actions verification passed.**
- Authorized scope: owner's 2026-09-05 M1 request and subsequent finalization request authorize normal M1 commits, push to the intended GitHub repository and actual CI verification. M2/M3, releases, package publication, access-setting changes and cloud provisioning remain unauthorized.
- Implemented: API validation/acceptance/status, SQL transactional outbox, Service Bus publisher/consumer, recoverable claims and durable attempts, separate receiver ledger/effect, Docker demo, eng verification and authored CI.
- Repository/branch: https://github.com/dahornea/relaylab, `main`. Existing license and bootstrap context retained. The committed candidate includes the reviewed application, tests, scripts, CI, lock files and bootstrap guidance; generated artifacts and local secrets are excluded and intentional synthetic test/emulator values are preserved.

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
- First remote CI: implementation commit `f47445b376253a12be42ace09cd68f6321d563ef` pushed normally to main; [run 33974639863](https://github.com/dahornea/relaylab/actions/runs/33974639863) failed overall during cleanup. Actual Ubuntu 24.04.4 / SDK 10.0.400 execution passed locked restore, warning-clean Release build, all 21 tests (0 failed/skipped) and the fresh-input demo (Delivered, one attempt/effect, repeat 200). Logs/TRX were downloaded and inspected under ignored artifacts/github/33974639863.
- Concrete CI failure: PowerShell on Unix treats `.env` as hidden, so plain Remove-Item refused to delete it after successful Compose resource cleanup. The demo now uses `Remove-Item -LiteralPath $envFile -Force` for that one generated file. A focused Linux PowerShell 7.6.4 check in the selected SDK container reproduced the original failure and executed the exact corrected script statement successfully; artifacts/publication/unix-env-cleanup.log records the result. No application code or integration tests were changed.
- Azure deployment: NOT RUN; M3 requires its own request and resource approval.
- Linux-host xUnit execution and complete workflow success are now verified by the passing correction run below. Windows local and Linux container demo evidence above remains valid; unrelated local suites were not repeated for publication.

## Passing remote CI and commit identity

- Initial implementation commit: `f47445b376253a12be42ace09cd68f6321d563ef` — `feat: implement RelayLab M1 webhook delivery`.
- Verified correction and tested implementation state: `3b4e498239c4173d1d84807c94f1e6fabe306253` — `fix: remove generated demo secrets on Linux`. This normal follow-up commit fixes cleanup without changing the application or tests.
- **Passing run:** [33975051554](https://github.com/dahornea/relaylab/actions/runs/33975051554), push event on main, tested exactly `3b4e498239c4173d1d84807c94f1e6fabe306253`, completed 2026-09-05. [Verify job](https://github.com/dahornea/relaylab/actions/runs/33975051554/job/101330168538) ran on GitHub-hosted **Ubuntu 24.04.4**, image `ubuntu-24.04`, with **SDK 10.0.400** and Linux PowerShell.
- Job logs and downloaded TRX confirm locked restore; Release build with **0 warnings, 0 errors**; **21 tests executed/passed, 0 failed, 0 skipped**, including all **15 SQL/broker-backed cases** and 6 validation cases. The test host ran on Linux, not just its dependencies. Results include concurrency, trigger-observed rollback, backlog, receiver restart deduplication, non-2xx/unknown timeout outcomes, terminal redelivery, expired claims, dead letters and real SQL/broker send/receive.
- Fresh-input demo rebuilt/published all three applications in Linux containers from hash-checked checkout inputs. Delivery `f9fe9c29-1127-451b-8f67-1e40622df255` reached **Delivered**, with **one attempt, one receiver effect**, and identical resubmission **200**. Owned containers/network/SQL volume and generated `.env` cleanup completed; the complete verification entry point exited successfully.
- Logs, TRX, input manifest and demo JSON were inspected from the run's `m1-verification` artifact. Downloaded local evidence is ignored under `artifacts/github/33975051554`; CI artifact retention is seven days. The run URL and tested commit identify remote evidence independently of local files.
- This README/STATUS evidence update is a **later documentation-only change**. It does not alter the application, tests, workflow or runtime configuration tested at `3b4e498`; evidence is tied to that implementation commit rather than implicitly to every later HEAD.
- The passing run emitted a nonblocking warning that pinned v4 actions declare Node 20; GitHub executed them with Node 24. The actions and all workflow steps succeeded.

## Review and remaining limits

- Read-only reviewer inspected source, intended untracked files, contracts, CI/scripts, TRX, build and demo logs. Verdict: no concrete M1 blocker; bounded review judgment, not proof of correctness.
- Its optional atomicity-oracle improvement was implemented and the affected real SQL check passed. No unresolved review finding remains.
- Prospective-commit review covered all 68 candidate files, including untracked inputs. Credential/private-path/generated-file checks passed, LICENSE remained byte-identical, and publication used normal commits/pushes without rewriting history or changing repository access settings. Generated `.env`/artifacts remain ignored and isolated verification containers/SQL volumes were cleaned up.
- M1 has one normal HTTP attempt, recoverable interrupted claims and outbox transport retries. Scheduled retries/replay, automatic dead-letter reconciliation, competing-worker crash matrices and telemetry are M2 work. Published work can remain incomplete after emulator restart or broker exhaustion; inspect status warnings and preserved dead letters. Receiver cooperation is necessary for idempotent effects.
- Local schema initialization uses EnsureCreated and is not a schema-upgrade mechanism. SQL CU14 is the tested local compatibility baseline, not a current production security recommendation. Authentication, identity, migrations, cloud operations and Azure acceptance remain M3 work.

## Next step

Run `pwsh -NoProfile -File ./eng/demo.ps1` to inspect M1; `pwsh -NoProfile -File ./eng/verify.ps1` is the full verification entry point. M2/M3 require a separate owner request. No release, package publication or Azure resources were created by this task.

## Update convention

Replace this current record as work progresses. Record milestone/authorization reference, meaningful completed work, selected environment/versions, exact tested commands, evidence location, unresolved risks and the next action. Distinguish local, remote CI and Azure evidence. Do not append full tool logs or duplicate the entire plan. A status note cannot create new owner authorization.
