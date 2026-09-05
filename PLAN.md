# Delivery plan

This file defines milestones; it does not authorize all of them at once. Current execution state is in docs/STATUS.md. The initial owner prompt authorizes M1. Keep subsequent requests bounded to M2 or M3.

## M1 — Local vertical slice and CI authoring

1. Read applicable instructions and inspect Git state, installed .NET SDKs, Docker Linux-container access and Codex configuration. Check model availability in the active client; do not silently switch away from the requested Astra model.
2. Run a small real SQL and Service Bus emulator connectivity check while selecting released compatible dependencies. Record the chosen SDK/runner/images; do not freeze an unavailable version into the plan.
3. Create the solution/projects described in ARCHITECTURE.md, lock dependencies and establish a warning-clean build. Keep scaffolding proportional.
4. Implement the acceptance transaction, unique idempotency key, conflict behavior and status endpoint. Verify concurrency against SQL Server.
5. Add publisher, queue consumer and durable sample-recipient ledger. Implement M1's explicit failure policy and recoverable claims.
6. Add the required tests in docs/TESTING.md and a scripted local demo. Use meaningful outcomes as assertions.
7. Create a GitHub Actions workflow and a local eng/ verification entry point. Align the runner/images with tested dependencies. Remote execution is a separate evidence item after an authorized push.
8. Run final M1 acceptance on the candidate, reproduce the developer flow from clean candidate inputs, perform focused review, fix concrete findings and update README.md/docs/STATUS.md. Stop at M1.

M1 acceptance:
- A new valid event returns 202 after persistence and is eventually Delivered to the sample.
- Identical retries return the original ID; concurrent retries produce one logical delivery; conflicting content returns 409.
- Failed acceptance cannot leave half of Delivery/OutboxMessage committed.
- Accepted work survives temporary publisher unavailability and is delivered after it resumes.
- The recipient's durable deduplication prevents a second effect for repeated DeliveryId.
- M1 HTTP failure/unknown-outcome reporting follows the documented policy.
- SQL integration and real-emulator end-to-end tests pass; the actual commands and environment are recorded.
- A fresh candidate checkout/copy runs the documented demo without hidden inputs.
- CI exists; its remote result is reported separately as executed or not yet executed.

## M2 — Recovery and observation

Begin only when requested, after assessing M1 evidence. Resolve the small scheduler/replay design using the existing invariants; routine implementation choices do not require an additional gate.

Implement persisted retry eligibility, explicit exhaustion, idempotent replay, concurrent-worker handling and OpenTelemetry. Execute the failure matrix in docs/TESTING.md. Capture one short repeatable demo with visible attempt history, a trace and recipient effect count. Review the candidate and record actual results.

M2 acceptance: crash windows and duplicates do not lose accepted intent or corrupt durable state; a cooperating receiver has one effect per logical delivery in the declared experiments; attempts/replay remain bounded and inspectable; ambiguous remote outcomes are labelled; incomplete experiments are not reported as pass.

## M3 — Cloud deployment and operation

Begin only when requested. Prepare Terraform, identity/access requirements, an immutable-image deployment workflow, migrations, smoke checks, rollback and teardown. Present the concrete resource plan and current cost estimate before provisioning. Owner approval applies to that concrete deployment scope.

After authorization: provision the environment, execute CI/CD, run cloud smoke/failure checks, demonstrate application revision rollback and tear down the test resources unless retention was authorized. Retain useful redacted evidence. Application rollback and database migration compatibility are evaluated separately.

M3 acceptance: real cloud execution, scoped identity, actual pipeline URL/commit/image identity, demonstrated rollback, measured cost/resource inventory and confirmed teardown/authorized retention. A Terraform file alone is not deployment evidence.

## Session completion and release

A milestone is complete when its scoped behavior and required checks pass and concrete review findings are resolved. Report unavailable external evidence explicitly rather than rerunning unrelated local tests.

Keep code uncommitted until asked to commit. Publication, tags, release assets and GitHub settings require session authorization. Before release, ensure the README describes implemented capabilities, a license is selected, dependencies/notices are appropriate and no private/local artifacts are included. Do not insert future work into the CV as completed experience.
