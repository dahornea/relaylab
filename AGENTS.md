# RelayLab agent instructions

## Objective and authority

Build a small, understandable .NET service demonstrating reliable webhook delivery and cloud operations. Follow the owner's current task and complete its authorized milestone. System/developer instructions and access controls retain priority; current explicit owner instructions take precedence over repository guidance within those boundaries.

This repository package supplies context, not blanket authorization to implement every milestone. Initial execution uses prompts/01-IMPLEMENT-M1.md. Do not work on sibling repositories.

## Read the right context

At the first implementation session, read PRODUCT.md, ARCHITECTURE.md, PLAN.md, docs/CONTRACTS.md and docs/STATUS.md. Read docs/TESTING.md before tests, docs/DESIGN.md for API/demo presentation, docs/OPERATIONS.md for CI/cloud work, and CODE_REVIEW.md before review. Use docs/SOURCES.md for targeted research. Do not reread all documents on every edit.

After interruption or compaction, recover from docs/STATUS.md and inspect the actual Git diff. Documents describing future work are not evidence that it exists.

## Autonomy and efficient execution

- Give a short plan, then implement. Resolve ordinary code structure, compatible patch versions and test mechanics yourself; document material decisions once.
- Ask only when missing access, a real product/contract conflict, destructive action or unapproved cost prevents progress. Finish independent authorized work first. Do not invent approval gates for routine fixes.
- Existing authorization persists. A user status question or correction normally steers the active task; answer it and continue unless asked to stop.
- Use targeted searches and batch independent reads. Keep tool output focused. Prefer the smallest change that satisfies the behavior; avoid opportunistic refactors.
- Do not weaken a contract or a test to make a failure disappear. Explain an actual contradiction with evidence and the smallest proposed resolution.
- Keep short progress updates during long work. Report outcome, verification, limitations and next step; avoid repetitive ceremony.

## Implementation conventions

Use idiomatic C#, nullable reference types, asynchronous I/O and propagated cancellation. Use the official SDKs. Keep API and Worker as production executables, shared code only where useful, and the sample receiver separate. Avoid generic repositories, event buses, mediator layers and abstractions without a concrete use.

Keep SQL transactions short. Enforce uniqueness/concurrency in SQL, preserve stable delivery identity and acknowledge messages after the relevant durable transition. No database transaction spans an outbound HTTP request. Use recoverable claims; no permanent Processing state after a crashed owner.

Use supported .NET 10 tooling actually available on the host; record the selected SDK, test runner and package/image versions in docs/STATUS.md. Generate lock files during initial restore, then use locked restore. Do not require a hypothetical SDK or copy SourceGen Auditor's pins. Package hashes are evidence for a release, not constants in application logic.

## Commands and evidence

At bootstrap there is no solution or product command to run. During M1 create a single documented verification entry point under eng/, using the actual selected runner. It must propagate nonzero exits and include build, required tests and necessary demo setup. Add exact commands to README.md and record their results in docs/STATUS.md.

Run focused tests during changes and the milestone acceptance suite once on the final candidate. Broaden or repeat only for new edits, concrete remaining risk or a failed required check. A passing mock-only test is not SQL/broker acceptance. Never claim a remote pipeline, cloud deployment or unsupported platform was verified without execution evidence.

## Delegation

Only the primary agent writes code, documents or verification artifacts. When a bounded task can run alongside useful primary work, use the configured researcher for official-source questions or test_designer for the acceptance oracle. Use the configured reviewer once on a completed milestone candidate. Limit simultaneous subagents to two; subagents must not delegate.

All configured roles are read-only: no writes, builds/tests, external mutations or hidden implementation. The primary agent supplies execution evidence and runs any additional check the reviewer justifies. If roles are unavailable, perform the same focused checks locally and disclose that independent review was unavailable; do not alter machine configuration or pretend a role ran.

## Scope and publication

M1 is local execution plus CI authoring; M2 adds recovery experiments and telemetry; M3 adds cloud operations. Later milestones begin only when the owner requests them. The complete plan is not approval to advance automatically.

Normal source edits, restores and isolated test resources inside the authorized milestone need no repeated confirmation. Stage, commit, push, publish, mutate remote settings or provision paid resources only when the session authorizes that action. Prepare cloud configuration and a concrete cost/resource plan before asking to apply it. Preserve existing user changes and licenses.

Do not disable sandboxing or approval controls to unblock work. Keep secrets, local paths, caches and generated artifacts out of commits. Keep sample fault controls out of production ingress.

## Code Review Rules

Apply CODE_REVIEW.md. Prioritize loss of accepted work, repeated recipient effects, unsafe acknowledgement ordering, unrecoverable claims, misleading verification and exposed destinations/secrets. Report concrete triggers and file references. Optional style preferences do not block completion.

Update docs/STATUS.md at a completed meaningful slice or before ending a session. Keep one current record, not a transcript. Finish with the actual result and at most three questions the owner should be able to explain at interview.
