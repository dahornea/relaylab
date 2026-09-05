# Product

## Purpose

RelayLab accepts a document-ready event, saves the intent to deliver it, and sends a webhook to a configured recipient. It exposes delivery status and attempts so an engineer can explain what happened during an outage or an ambiguous response.

The portfolio goal is evidence of .NET backend ownership: durable data, asynchronous messages, failure recovery, tests, CI/CD and cloud operation. No adoption, performance or production maturity claim is made by the bootstrap.

## Initial user and workflow

One developer/operator runs one service with one event type, document.ready, and one preconfigured destination named demo. The sample receiver records an example business effect in its own database.

1. Submit an event with an idempotency key.
2. Receive a delivery identifier after persistence succeeds.
3. Query its status and attempts.
4. Observe the recipient's effect and diagnose the result.
5. In M2, induce failures and replay an exhausted delivery deliberately.

## Milestones

| Milestone | Product result |
| --- | --- |
| M1 | Local durable acceptance and delivery, idempotent receiver, meaningful tests and CI workflow |
| M2 | Durable retry policy, inspectable exhaustion/replay, crash and concurrency evidence, traces |
| M3 | Authenticated Azure deployment, IaC, executed CD, smoke test, application rollback and teardown |

M1 is a functional slice with basic failure recording, not the complete reliability demonstration. Exact contracts are in docs/CONTRACTS.md; acceptance is in PLAN.md.

## Scope limits

The first release has no custom frontend, tenancy/billing, generic workflow language, multiple brokers, multi-cloud support, Kubernetes, event sourcing or general-purpose messaging library. Local telemetry uses an existing viewer. A future optional inspector concept in docs/DESIGN.md is not scheduled implementation.

Destinations are configuration, not arbitrary request URLs. The service does not infer whether another system processed a timed-out request. Repeated requests are possible; recipient cooperation is required for idempotent effects.

## Completion that matters

A fresh checkout runs a clear demo. An engineer can show a failure, locate it in durable state and telemetry, recover, and explain the remaining limits. The deployment milestone supplies actual pipeline and Azure evidence. The owner can explain the implementation without reading generated documentation aloud.

The project timebox is three weeks alongside work, subject to reassessment after M1. Reduce optional presentation and measurement breadth if necessary. Do not inflate claims or defer applications until the repository is perfect.
