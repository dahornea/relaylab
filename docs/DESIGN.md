# Experience and presentation design

## Current product experience

The first version is an API and background service. Its user experience consists of clear submission/status responses, actionable errors, a reproducible demo and inspectable telemetry. No custom frontend is included in M1–M3.

Optimize for the engineer's questions:
- Was my event accepted durably?
- Which delivery does a repeated submission refer to?
- What was attempted, and what is known about the outcome?
- Why is it waiting or failed, and what recovery action is available?

Use consistent identifiers, UTC timestamps and the same status vocabulary in responses, logs, tests and documentation. Explain an HTTP error with the action the caller can take. Use ordinary ProblemDetails responses and stable error codes. Keep stack traces and implementation detail out of caller errors.

## Demo presentation

M1's README starts with a short description, prerequisites and one copy-ready startup/demo path. Prefer short multiline commands over horizontally scrolling output. Label example output versus actual recorded output. Show delivery ID, status and receiver effect count.

M2's five-minute demonstration follows one delivery through acceptance, failure, recovery and effect inspection. Use the existing Aspire telemetry dashboard or another already supported OTLP viewer. Arrange the demonstration around a selected trace and relevant attempt history; avoid adding a custom monitoring UI.

Record the viewport and commands so screenshots/video are readable. Show status text, not color alone. Explain why an ambiguous timeout differs from a known rejection. Document local setup cleanup and a common-error troubleshooting table based on actual failures.

## Optional future inspector concept — not scheduled

If the owner later requests a UI, start with one read-only delivery inspector instead of a general dashboard. This concept is planning only and does not authorize frontend scaffolding.

| Region | Contents |
| --- | --- |
| Header | Environment, connection state and refresh control |
| Delivery list | ID search, status filter, accepted time and latest result |
| Selected delivery | Status, immutable identity, attempts in time order, safe errors and trace link |

Use compact readable typography, neutral surfaces and restrained status accents. All states need text labels, keyboard access, visible focus, sufficient contrast and responsive layout. Include loading, empty, unavailable, stale and failed states. Do not display secrets or raw payloads by default. Replay is a later explicit action with clear consequences and duplicate-submit handling, not part of the read-only concept.

Choose a frontend stack only if this optional work is actually requested. No React/Blazor packages or design-system dependency are justified by the current release scope.
