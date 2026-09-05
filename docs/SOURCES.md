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
