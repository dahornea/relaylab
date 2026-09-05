# Decision notes

Keep this file short. Record only choices whose rationale a maintainer needs, with their impact and actual verification. Do not create an ADR for each implementation detail.

## Initial selected choices

| Choice | Reason | Limit |
| --- | --- | --- |
| API and Worker, one shared application database | Separate request lifetime from asynchronous work | Not a general microservice platform |
| SQL transactional outbox | Persist delivery intent with acceptance | Broker publication can repeat |
| Official Service Bus SDK and small application-specific dispatcher | Make the failure boundaries inspectable | Not a reusable framework claim |
| Durable receiver receipt and effect | Demonstrate recipient-side idempotency | Requires a cooperating recipient |
| Compose and Testcontainers | Developer startup and isolated automated integration | Emulator is not the Azure service |
| GitHub Actions, then Terraform/Azure | Show the delivery/operations lifecycle publicly | Remote/cloud claims need real runs |
| Existing telemetry viewer | Show diagnostic evidence with low UI effort | Optional custom inspector is unscheduled |

## M1 decisions

- Use the installed SDK 10.0.400 with latestPatch roll-forward, EF Core 10.0.11, Azure.Messaging.ServiceBus 7.20.2 and Testcontainers 4.14.0. xUnit 2.9.3 with its VSTest adapter 3.1.5 and Microsoft.NET.Test.Sdk 18.9.0 supplies one runner. Lock files record transitive versions; `eng/verify.ps1` uses locked restore. This compatible runner avoids mixing MTP and VSTest conventions.
- SQL key uniqueness uses varchar(128), Latin1_General_100_BIN2 and validation excluding whitespace/nonvisible ASCII. Typed identity comparisons use ordinal .NET string equality. An explicit transaction commits two writes; SQL uniqueness resolves concurrent first submissions. A test-only SQL trigger forces a failure after the delivery write to exercise real rollback.
- Claims use conditional SQL updates, random owner tokens and SQL SYSUTCDATETIME. HTTP results require the same current work and unexpired owner inside a short transaction with the attempt update. Started-but-interrupted attempts retain an unknown end time and are marked Interrupted on recovery. Local code never holds a transaction across HTTP or Service Bus.
- One outbox UUID is the initial work identity and broker MessageId. The direct SDK makes the non-atomic SQL/broker boundary visible without adding a general messaging framework. Outbox transport retries are durable; ordinary HTTP retries/replay are absent in M1. See contracts for exact bounds.
- `EnsureCreated` runs only through explicit local `--init-db` commands and test setup; application requests do not migrate schema. It is sufficient for this first local schema. Versioned migrations and deployment coordination must be designed before M3; do not use EnsureCreated to evolve an existing schema.
- Compose and Testcontainers read the same queue configuration and image inventory. SQL 2022 CU14 is the concrete compatibility baseline used by the Testcontainers Service Bus builder, not a claim of the latest SQL security patch. SQL databases for RelayLab, receiver and emulator remain distinct on one local SQL instance. The emulator is pinned to 2.0.0 and loses messages on restart.
- Status exposes possible incomplete/broker-exhausted work without claiming dead-letter reconciliation. The worker's read-only `--deadletters` command preserves messages. Full reconciliation and retention/paginated history are M2 decisions.
- All ingress is local; executable startup rejects the default Production environment. Default Compose publishes only loopback ports and uses an ignored generated local password. Payloads, SQL exception details and destination URLs are excluded from application logs. No signing, cloud identity or production-security claim is made.

## Resolve when M2 is requested

Persisted retry scheduling, work/attempt/replay identities, budget classification, dead-letter reconciliation and replay request details. Explain crash windows and the oracle for each meaningful choice.

## Resolve when M3 is requested

Actual Azure resource/SKU plan, ingress/recipient authentication, identity scoping, migrations, rollback and retention/teardown. Paid execution and external access changes require concrete authorization.
