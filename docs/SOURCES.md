# Official sources and dependency selection

Research checkpoint: 2026-09-05. Use these sources for the stated question and check current package/client compatibility on the implementation host. This is a starting source map, not evidence that the product has been built.

## Codex

- [GPT-6 Astra guidance](https://developers.openai.com/api/docs/guides/latest-model): model identity and model-specific prompting behavior.
- [Configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference): project configuration, role layers and reasoning controls.
- [Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents): role configuration and delegation behavior.
- [AGENTS.md discovery](https://learn.chatgpt.com/docs/agent-configuration/agents-md): scoped project instructions.
- [Best practices](https://learn.chatgpt.com/guides/best-practices): concise reusable guidance and evidence-led development.
- [Codex JSON Schema](https://developers.openai.com/codex/config-schema.json): structural validation of the supplied TOML. Runtime/client support remains a separate check.

## .NET and messaging

- [EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions): database atomicity and transaction/execution-strategy interaction.
- [Service Bus delivery and duplicates](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-message-loss-and-duplicates): acknowledgement and receiver idempotency.
- [Service Bus duplicate detection](https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection): finite send-deduplication scope.
- [Service Bus .NET quickstart](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dotnet-get-started-with-queues): official client and identity setup.
- [Emulator limitations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator): development-only differences and restart data loss.
- [Testcontainers SQL Server](https://dotnet.testcontainers.org/modules/mssql/): isolated database test lifecycle. Example runner packages are examples, not a required mixed runner configuration.
- [xUnit MTP guidance](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform): check compatible current packages if selecting MTP.
- [.NET OpenTelemetry](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel): framework instrumentation and exporters.

## Deployment

- [Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/overview): API/background hosting and revisions.
- [Terraform on Azure](https://learn.microsoft.com/en-us/azure/developer/terraform/overview): providers and resource management.
- [GitHub to Azure OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect): federated deployment authentication.

## Dependency policy

Use released compatible .NET 10 and EF Core SQL Server packages, Azure.Messaging.ServiceBus, a single xUnit runner and the minimum Testcontainers modules needed. Add OpenTelemetry packages in M2, deployment providers in M3. SDK selection, exact direct/transitive package versions and container image references are recorded after real restore/build/connectivity checks. Commit lock files; do not pin nonexistent versions or require universal byte-identical application binaries across commits.

Prefer a relevant official example over a new abstraction. Research stops when the concrete uncertainty is resolved; it does not need a recurring broad market scan.

## M1 resolved sources

- [Emulator release notes](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-emulator-whats-new) and [local setup](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator): selected emulator 2.0.0, configuration, AMQP and health ports.
- [Testcontainers MsSql 4.14.0 source](https://github.com/testcontainers/testcontainers-dotnet/blob/4.14.0/src/Testcontainers.MsSql/MsSqlBuilder.cs) and [ServiceBus package](https://www.nuget.org/packages/Testcontainers.ServiceBus/4.14.0): SQL CU14 baseline and released module. Confirmed API against restored package; actual SQL/broker probe recorded in STATUS.
- [EF Core SQL Server 10.0.11](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.11), [Service Bus SDK 7.20.2](https://www.nuget.org/packages/Azure.Messaging.ServiceBus/7.20.2), [xUnit 2.9.3](https://www.nuget.org/packages/xunit/2.9.3): released dependency versions, confirmed by NuGet restore and package lock files.
- Official GitHub v4 ref endpoints were read on 2026-09-05 for [checkout](https://api.github.com/repos/actions/checkout/git/ref/tags/v4), [setup-dotnet](https://api.github.com/repos/actions/setup-dotnet/git/ref/tags/v4) and [upload-artifact](https://api.github.com/repos/actions/upload-artifact/git/ref/tags/v4). The resulting commit SHAs are pinned in `.github/workflows/ci.yml`. This establishes authored references, not remote CI execution.

## M2 resolved sources

- [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/) and [OTLP exporter 1.18.0](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol/1.18.0): official SDK integration and selected released package. Hosting/exporter dependencies were restored and locked.
- [Standalone Aspire dashboard](https://aspire.dev/dashboard/standalone/) and [configuration](https://aspire.dev/dashboard/configuration/): container OTLP gRPC port 18889, frontend 18888 and local anonymous access. Only the frontend is published, on loopback; OTLP stays in the Compose network.
- [Microsoft image tags](https://mcr.microsoft.com/v2/dotnet/aspire-dashboard/tags/list): actual published image 13.5.2. The repository's later 13.5.3 release did not have a matching dashboard container; its failed pull is not a successful verification.
- [Dashboard 13.5.2 routes](https://github.com/microsoft/aspire/blob/v13.5.2/src/Aspire.Dashboard/DashboardEndpointsBuilder.cs) and [telemetry service](https://github.com/microsoft/aspire/blob/v13.5.2/src/Aspire.Dashboard/Api/TelemetryApiService.cs): selected viewer's `/api/telemetry/traces` query and OTLP JSON response. The demo checks real ingested lifecycle spans, services and shared trace identity. Metrics are also available through the viewer's Metrics/Table page.

## M3 resolved sources (2026-09-05)

- [AzureRM 5.4.0 provider source/docs](https://github.com/hashicorp/terraform-provider-azurerm/tree/v5.4.0/website/docs/r) and [AzureAD 3.9.0](https://github.com/hashicorp/terraform-provider-azuread/tree/v3.9.0/docs): actual Container Apps jobs/revisions, SQL Entra-only administration, state storage, federation and resource app schemas. Both roots validated using these providers and Terraform 1.16.1; this is local schema validation, not Azure deployment.
- [Azure Blob backend](https://developer.hashicorp.com/terraform/language/backend/azurerm), [Azure GitHub OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect) and [GitHub environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments): state container data role, exact environment federation subject, branch/reviewer protection. Owner bootstrap remains separate from OIDC runtime deployment.
- [SQL managed identities](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-azure-ad-user-assigned-managed-identity?view=azuresql), [SqlClient authentication](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication?view=sql-server-ver17) and [SQL object grants](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-object-permissions-transact-sql?view=sql-server-ver17): schema administrator, explicit object-ID SID creation without Graph lookup, client-ID-based managed identity acquisition and table-scoped permissions. Actual Azure principal creation/login is unverified.
- [Client credential ACL authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow#access-control-lists) and [optional claims](https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference): app-only tenant/object-ID checks, idtyp and resource-side authorization of role-less tokens. [ACR managed identity](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull) and [Service Bus RBAC](https://learn.microsoft.com/en-us/azure/service-bus-messaging/authenticate-application): explicit workload identities and narrow data roles.
- [Azure Monitor Entra authentication](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication) and [exporter 1.9.0 source](https://github.com/Azure/azure-sdk-for-net/tree/Azure.Monitor.OpenTelemetry.Exporter_1.9.0/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter): Monitoring Metrics Publisher, credential configuration, Internal spans as dependencies, service role mapping and exact custom metric names. [Custom metric queries](https://learn.microsoft.com/en-us/azure/azure-monitor/app/metrics-overview#custom-metrics): valueSum/valueCount; exported histogram aggregates are not individual buckets/percentiles.
- [Container Apps revisions](https://learn.microsoft.com/en-us/azure/container-apps/revisions) and [job CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp/job?view=azure-cli-latest): staged manual schema execution and immutable old-image rollout as new Single-mode revisions. Cloud scripts remain unexecuted.
- [setup-terraform v3](https://github.com/hashicorp/setup-terraform/tree/v3), [azure/login v2](https://github.com/Azure/login/tree/v2) and [actionlint 1.7.12](https://github.com/rhysd/actionlint/releases/tag/v1.7.12): official release refs resolved before SHA pinning; actionlint archives verified against official SHA256 checksums. [CLOUD.md](CLOUD.md) records current official retail-price sources and assumptions instead of treating estimated cost as measured billing.
