# Focused review

Review the current milestone's candidate, including new untracked files and relevant callers. Use execution evidence supplied by the primary agent. A read-only reviewer does not build or run tests.

## What deserves attention

- Can acceptance return success before Delivery and OutboxMessage are committed together?
- Can concurrent identical requests create two logical deliveries, or conflicting inputs bypass the idempotency contract?
- Can a crash lose durable work, leave an unrecoverable claim, or let a stale owner overwrite current state?
- Can acknowledgement remove the last recovery path before the durable transition?
- Can a duplicate HTTP call repeat the receiver effect or be hidden by misleading status?
- Are message identity, retry eligibility, transport redelivery and replay generation handled consistently for the current milestone?
- Are any request-supplied destinations, redirects, secrets, fault endpoints or cloud development settings exposed incorrectly?
- Do tests exercise real SQL/broker behavior, and are emulator/cloud limitations stated accurately?
- Does the documented demo use actual candidate inputs and pass with the stated environment?
- Is a public claim unsupported, or does a concrete complexity problem impede correctness or maintenance?

## Report

For each finding provide severity, file reference, triggering condition, impact and the smallest viable correction. State any missing evidence. Keep optional improvements separate. Do not require stylistic preferences, speculative abstractions, arbitrary coverage percentages or a fixed test count.

When no concrete blocker remains, say so with the reviewed scope and verification limitations. The verdict is a review judgment, not proof of correctness.

The primary agent resolves findings. Repeat only the affected review/checks after a fix, expanding when the fix creates a concrete additional risk. Do not start open-ended automatic review loops. Record the final disposition concisely in docs/STATUS.md.
