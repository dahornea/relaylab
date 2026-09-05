# Optional M3 cloud preparation — deferred

**The owner has chosen not to create an Azure subscription or deploy resources.** This retained procedure is locally validated preparation, **not Azure execution evidence or an active approval request**. Clone, build, tests, Docker demonstrations and ordinary CI require no Azure account, CLI or credentials. The cloud workflow has only `workflow_dispatch`; normal CI neither calls it nor dispatches it, and no schedule, push, pull-request or workflow-run trigger is present.

Do not execute the external commands below under the current local-only scope. A future decision to resume cloud work requires new explicit resource/permission approval and refreshed prices, tooling and policy checks. Azure deployment, effective permissions, billing, rollback and teardown remain unexecuted. Original M3 cloud acceptance is incomplete; useful infrastructure/code preparation is preserved.

## Proposed inventory and cost

Propose **West Europe**, one **8-hour** demonstration, then complete teardown. Use an owner-selected globally unique lowercase prefix, 6–16 characters. Two resource groups separate application resources from the state/deployment identity that must outlive their removal.

| Resource | Size and purpose | 8 hours USD | 730 hours USD |
| --- | --- | ---: | ---: |
| ACR | Basic, admin credentials disabled; three image repositories, keep total under included 10 GB | 0.056 | 5.067 |
| Azure SQL | One logical server; two Basic 5-DTU databases, 2 GB each; delivery and receiver ledgers; local backup redundancy, 7-day recovery window | 0.107 | 9.794 |
| Service Bus | One Standard namespace and deliveries queue; SAS disabled, 1 GB queue | 0.108 | 9.812 |
| Container Apps | One Consumption environment, API/worker/receiver each 0.25 vCPU / 0.5 GiB; min=max=1 | 0.907 | 82.782 |
| Schema jobs | Two manual jobs, each 0.25 vCPU / 0.5 GiB; estimate 10 minutes per job | 0.013 | 0.013 |
| Terraform state | Standard StorageV2 Hot LRS, assume 1 GB and 1,000 reads + 1,000 writes; Entra auth, private blob container, versioning and 7-day soft delete | 0.006 | 0.025 |
| Telemetry | Linked Application Insights / Log Analytics, assume 0.1 GB for demo or 1 GB/month at $2.99/GB; 30-day retention | 0.299 | 2.990 |
| Internet egress | Assume 0.1 GB / 1 GB at $0.087/GB | 0.009 | 0.087 |
| HTTPS requests | Assume 10,000 / 100,000 requests at $0.56/million | 0.006 | 0.056 |
| **Gross estimate** | Before free grants, taxes and discounts | **1.51** | **110.63** |

Rates were checked on **2026-09-05** using the official [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices), region `westeurope`, USD Consumption meters. Compute uses $0.000034/vCPU-second and $0.000004/GiB-second. Active rates are deliberately charged for all three replicas. Reference pricing: [Container Apps](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [SQL](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/), [Service Bus](https://azure.microsoft.com/en-us/pricing/details/service-bus/), [ACR](https://azure.microsoft.com/en-us/pricing/details/container-registry/), [Storage](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/), [Monitor](https://azure.microsoft.com/en-us/pricing/details/monitor/), [Bandwidth](https://azure.microsoft.com/en-us/pricing/details/bandwidth/).

Unused subscription/month Container Apps grants (180,000 vCPU-seconds, 360,000 GiB-seconds and 2 million requests), account/month 5 GB logs and first 100 GB egress would reduce this to approximately **$0.28 / $99.94**. Do not assume those grants remain unused. ACR daily billing rounding can add about $0.11 to the short run. Two deployments plus rollback run additional short schema jobs and briefly overlap revisions; budget **$2 expected / $10 approved ceiling for eight hours**, with teardown rather than indefinite retention. A month costs roughly **$111**, or **$130 contingency**; a high-volume environment can exceed this estimate. Daily log caps are delayed safeguards, not spending limits. GitHub hosted-runner minutes/artifact storage depend on repository plan and available allowance; no paid runner is proposed. Check remaining allowance before dispatch.

No VNet, private endpoint, NAT gateway, dedicated compute plan, extra scheduler, Key Vault, paid alert, cloud Aspire server or registry build task is needed. Managed identities, resource groups and the SQL logical server have no separate hourly meter. Azure may create Container Apps platform-managed resources; inventory these and confirm their removal with the environment. State incurs storage cost until bootstrap teardown, including retained versions. Registry layers, SQL, Service Bus and logs continue billing when application traffic stops.

## Identity and network boundary

| Principal | Exact intended access |
| --- | --- |
| Owner bootstrap login | Create the two resource groups and their resources; create/delete scoped role assignments; register required resource providers; create and own two Entra applications/service principals. The owner also receives Storage Blob Data Contributor on the new state account. |
| GitHub deployment UAMI | Contributor and Role Based Access Control Administrator **on the application RG only**; Storage Blob Data Contributor **on its state container only**; AcrPush on its registry. No subscription Owner and no Microsoft Graph grant. Role assignment administration is powerful within this single RG and is explicitly part of approval. |
| API UAMI | AcrPull; Monitoring Metrics Publisher on Application Insights; SQL CONNECT, schema marker SELECT, delivery SELECT/INSERT/UPDATE, outbox SELECT/INSERT, attempt SELECT, replay SELECT/INSERT. |
| Worker UAMI | AcrPull; Monitoring Metrics Publisher; Service Bus Data Sender and Data Receiver on **deliveries queue only**; SQL CONNECT, marker SELECT, delivery SELECT/UPDATE, outbox/attempt SELECT/INSERT/UPDATE. |
| Receiver UAMI | AcrPull; Monitoring Metrics Publisher; receiver SQL CONNECT, marker SELECT, receipts/effects SELECT/INSERT. |
| Schema UAMI | AcrPull and Entra-only SQL server administrator. Only the two explicit jobs use it; it has DDL authority on both new databases. |
| SQL server system identity | No Graph permissions. SQL users are created with explicit managed-identity object-ID SIDs rather than directory lookup. |

Bootstrap needs subscription **Contributor plus role-assignment authority** (Owner or RBAC Administrator at an appropriate parent scope); an administrator can instead predelegate just those capabilities for the approved RGs. Provider registration requires subscription scope. Tenant policy must permit creating owned app registrations and service principals; otherwise a tenant administrator performs the reviewed bootstrap with an appropriate application-administrator role. Do not grant the workflow Graph directory write or copy credentials into GitHub.

GitHub OIDC federation is exactly issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, subject `repo:dahornea/relaylab:environment:relaylab-demo`. The new GitHub environment permits only the `main` branch and requires the selected owner/reviewer before every cloud job. Self-review remains permitted for this single-owner demonstration. Required reviewers must be available under the repository's GitHub plan; if unavailable, stop and resolve the protection requirement instead of dropping it. Four environment **variables**, no secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `M3_CONFIGURATION`. The deploy identity cannot edit its federation or bootstrap RG. Workflow token permissions are contents/actions read and id-token write. Deployment is manual and requires successful ordinary CI for the exact SHA.

The two single-tenant Entra resource apps request v2 access tokens and the `idtyp` optional claim. No delegated scope or reusable webhook secret is introduced. API endpoints validate RS256 signature, exact tenant issuer/audience, expiry and exact deployment identity `oid`, requiring `idtyp=app` and no `scp`. Receiver `/webhooks` permits only worker `oid`; receipt diagnostics permit only deployment `oid`. The tenant/object-ID ACL is deliberate: resource apps allow role-less app token issuance but **issuance itself does not grant endpoint access**. The worker obtains a managed-identity token only for the exact configured HTTPS receiver URI, with redirects disabled. A separate human `az login` token is intentionally rejected by the data API. Use the OIDC cloud workflow for verification.

API/receiver have public HTTPS Container Apps ingress; worker has none. Anonymous liveness/readiness reveal only minimal health/schema information. SQL uses TLS with certificate validation and Entra-only managed identities. Its `0.0.0.0` Azure-services firewall rule admits network connections from **other Azure tenants too**; authentication still applies. Service Bus/ACR/state have public service endpoints and identity access controls. This minimal demo does **not** provide private network isolation or general multi-customer ingress. Subscription policy requiring private endpoints is an unresolved owner constraint, not a reason to bypass policy.

## Explicit schema and application rollback

Local M2 still needs a fresh M2 schema and rejects M1. **Cloud M3 needs new empty databases**, initialized by `--deploy-schema`; it refuses unversioned M1 **and M2** databases without altering their data. No migration/adoption of existing databases, reset, or down migration is provided. Preserve existing user databases separately.

Schema version 1 records the database kind and normalized EF-generated DDL hash. A SQL application lock serializes initialization; tables and marker commit in one transaction. The jobs rerun safely, then create/verify the runtime principal SIDs and grant the listed table permissions. Startup only verifies the marker/hash; runtime replicas never create tables. Partial identity grants can be repaired by rerunning the schema job. Runtime identities have no DDL/DELETE grant. The marker detects incompatible image models, not arbitrary administrator edits to the physical schema.

Deployment has three stages: foundation with no app/jobs; publish immutable digests and execute schema jobs; roll out applications after both jobs succeed. An existing deployment keeps its current images while candidate schema binaries verify compatibility. Deployment failure leaves accepted SQL work retained; collect diagnostics and rerun or explicitly roll back. Never delete state/databases to repair a failed job.

Rollback takes a previously **verified M3** release artifact, validates the registry/digests/environment, runs that image's schema check, and deploys its three old digests as **new Single-mode revisions**. SQL is not rolled back. Different version/hash or unversioned M1/M2 images are incompatible. This conservative baseline forbids schema-changing rollback; a future schema evolution needs a separately reviewed forward migration/compatibility plan. Single mode keeps one serving revision; transient old/new overlap is handled by existing SQL lease fencing and durable receiver deduplication. The first release has no previous compatible release: demonstrate rollback after two verified schema-compatible releases. Do not report reapplying the same image as rollback.

## Local preparation commands

Use PowerShell 7 in the checkout, .NET 10.0.400 and a Docker Linux engine. Install official [Terraform 1.16.1](https://releases.hashicorp.com/terraform/1.16.1/) on PATH after verifying its published checksum; this task used a portable copy under ignored `artifacts/m3/tools/terraform`. Providers are pinned to AzureRM **5.4.0**, AzureAD **3.9.0**, with Windows/Linux amd64 checksums in both lock files. In this prepared Windows workspace, make the portable copy available with `$env:PATH = (Join-Path $PWD 'artifacts/m3/tools/terraform') + [IO.Path]::PathSeparator + $env:PATH`.

```powershell
./eng/install-actionlint.ps1
./eng/verify-m3.ps1 -StaticOnly
./eng/verify-m3.ps1
```

The installer checksum-verifies official actionlint 1.7.12 and adds it to PATH for that PowerShell process. If using a separate `pwsh -File` invocation, add `artifacts/m3/tools/actionlint` to that shell's PATH first. Static verification runs Terraform fmt, backend-disabled init/validate, actionlint and parsing of all PowerShell scripts. Full verification also runs locked NuGet restore, warning-clean Release build, the unfiltered SQL/broker suite and Linux container builds/fresh recovery demo. No Azure login, plan or resources are needed. Terraform validate is **not** an Azure plan or proof that subscription policies/quotas allow the configuration.

## After approval: exact execution sequence

Keep owner IDs in environment/local ignored files. Never paste tokens, passwords or Terraform state into chat. Owner inputs: subscription ID, tenant ID, globally unique prefix, numeric GitHub reviewer ID, confirmation of permissions/plan features and the eight-hour/$10 teardown scope. Confirm region and quotas before apply. Commands below intentionally require these values:

```powershell
$subscription = $env:RELAYLAB_SUBSCRIPTION_ID
$tenant = $env:RELAYLAB_TENANT_ID
$prefix = $env:RELAYLAB_NAME_PREFIX
$reviewer = [long]$env:RELAYLAB_GITHUB_REVIEWER_ID
az login --tenant $tenant
az account set --subscription $subscription
# One-time registration; no automatic broad provider registration in Terraform.
foreach ($provider in @('Microsoft.Resources','Microsoft.Storage','Microsoft.ManagedIdentity','Microsoft.Authorization','Microsoft.ContainerRegistry','Microsoft.Sql','Microsoft.ServiceBus','Microsoft.App','Microsoft.OperationalInsights','Microsoft.Insights')) {
    az provider register --namespace $provider --wait
    if ($LASTEXITCODE -ne 0) { throw "Provider registration failed: $provider" }
}
./eng/cloud/bootstrap.ps1 -SubscriptionId $subscription -TenantId $tenant -NamePrefix $prefix -Action Plan
# Review saved plan against this approved inventory; Apply replans and applies current inputs.
./eng/cloud/bootstrap.ps1 -SubscriptionId $subscription -TenantId $tenant -NamePrefix $prefix -Action Apply
./eng/cloud/configure-github.ps1 -ConfigurationPath artifacts/cloud/configuration.json -ReviewerUserId $reviewer
```

Bootstrap state is deliberately local (`infra/bootstrap/terraform.tfstate`) because its storage does not yet exist. **Make an encrypted owner-controlled backup of that state and configuration immediately and retain it through teardown**; do not upload it as a CI artifact or delete it after main deployment. The main Azure Blob backend is created by bootstrap, uses Entra authentication, blob leasing, versioning and soft delete. GitHub accesses it through OIDC. Owner login must have the new data role propagated before accessing storage. If propagation delays a step, inspect and rerun; do not add a credential fallback. Azure CLI is a prerequisite for owner execution; the preparation inspected release 2.90.0 metadata but did not install/login. Workflow logs its actual CLI and installs the official Container Apps/Application Insights extensions; their Azure execution remains unverified.

Commit/push the reviewed files **only with separate Git authorization**, observe exact-SHA `ci.yml` success, then dispatch and approve the environment job:

```powershell
gh workflow run cloud.yml --repo dahornea/relaylab --ref main -f operation=deploy
gh run list --repo dahornea/relaylab --workflow cloud.yml --limit 5
# Set the actual run ID, then inspect result, logs and redacted artifacts.
gh run view $env:RELAYLAB_RUN_ID --repo dahornea/relaylab --log
gh run download $env:RELAYLAB_RUN_ID --repo dahornea/relaylab --name cloud-evidence --dir artifacts/cloud/observed
gh workflow run cloud.yml --repo dahornea/relaylab --ref main -f operation=verify
```

Deploy builds three linux/amd64 images using the existing nonroot Dockerfile and locked restore, pushes commit tags, resolves and uses ACR `@sha256` digests. It records source SHA, images, revisions and schema version only after cloud verification succeeds. No mutable `latest` deployment. The verification checks anonymous 401s, worker/operator separation, fresh acceptance/repeat, status and receiver effect, three 503 attempts/exhaustion, replay 202/200 identity, receiver committed effect, worker/receiver revision restarts and eventual Delivered with Unknown history and one effect. CLI restart latency may let the HTTP deadline win; the evidence records actual pre-restart SQL state and does **not** claim an exact Azure SIGKILL window. Local M2 retains that stronger synchronized process-kill experiment.

Cloud telemetry must be actually ingested in Application Insights across all services. Delivery-tagged rows identify the complete trace, including HTTP child spans that do not inherit custom tags. A pre-restart query preserves required completed spans before buffered exporters are replaced. A separate `customMetrics` query checks positive API/receiver/worker counters and worker HTTP histogram samples before receiver replacement; it does not infer metric ingestion from traces. SQL remains the source of truth; telemetry loss does not justify inventing outcomes. Review actual Azure identity logins, role inventory, schemas, logs, run SHA/digests and measured cost before calling cloud acceptance passed. No Azure result is implied by the authored queries.

After a second distinct schema-compatible release passes, use the earlier successful deployment run:

```powershell
gh workflow run cloud.yml --repo dahornea/relaylab --ref main -f operation=rollback -f rollback_run_id=$env:RELAYLAB_PRIOR_DEPLOY_RUN_ID
```

The workflow checks that the artifact came from successful `cloud.yml` on main and matches its run SHA. Inspect new revisions, previous digests, successful smoke and retained data; do not call this a database rollback. Seven-day artifacts can expire: preserve the reviewed redacted release evidence securely for any longer retention. No rollback demonstration is possible until the second distinct release exists; creating that release also needs Git authorization.

## Teardown and evidence

Unless retention is explicitly approved, destroy application resources before removing their backend and deployment identity. This deletes **only the disposable approved cloud databases**, including receiver receipts; preserve redacted evidence first. Existing local/user databases are outside both stacks.

```powershell
gh workflow run cloud.yml --repo dahornea/relaylab --ref main -f operation=teardown -f teardown_resource_group="$prefix-app"
# Observe the actual run and download teardown.json before removing the environment.
./eng/cloud/bootstrap.ps1 -SubscriptionId $subscription -TenantId $tenant -NamePrefix $prefix -Action DestroyPlan
./eng/cloud/bootstrap.ps1 -SubscriptionId $subscription -TenantId $tenant -NamePrefix $prefix -Action Destroy
gh api --method DELETE repos/dahornea/relaylab/environments/relaylab-demo
```

The first step saves private state locally inside the runner (not uploaded), destroys main state resources and requires an empty application RG. Bootstrap destroy refuses while that RG contains resources, then removes both RGs, state storage, deploy identity/federation, role assignments and the two Entra apps/SPs. Retain the owner's bootstrap state until both groups are confirmed absent; inspect Entra enterprise-app/app-registration removal too. Deleting the GitHub environment removes its variables/protection settings. Repository workflows remain as reviewable source; no release/package is created. Confirm Container Apps platform-managed resources and cost/resource inventory, including absence of state/registry, rather than merely a green destroy command. Billing data can lag: collect final measured charges when available and label delayed totals honestly. Never delete a shared/unrecognized resource to force an empty inventory.

Application teardown/rollback scripts also support explicit owner invocation, but data-plane smoke requires the authorized deployment workload identity; a human token must not be substituted or granted broader ingress. Use the reviewed OIDC workflow by default.
