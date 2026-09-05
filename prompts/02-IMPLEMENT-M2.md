I authorize RelayLab M2: recovery and observation. Do not start M3.

Read AGENTS.md, current docs/STATUS.md and the M2 portions of PLAN.md,
ARCHITECTURE.md, docs/CONTRACTS.md, docs/TESTING.md and docs/DESIGN.md. Inspect
the actual M1 implementation and evidence; resolve a concrete M1 prerequisite
defect if needed without broad refactoring. Preserve unrelated changes.

Briefly choose the smallest durable retry scheduler and define work/attempt/
replay identities, budget classification and dead-letter reconciliation.
Record meaningful choices once in docs/DECISIONS.md and extend replay contracts.
Proceed autonomously within the established delivery guarantees.

Implement durable retry eligibility, bounded exhaustion, idempotent explicit
replay, competing-worker safety and OpenTelemetry. Execute the M2 failure matrix
using controlled synchronization and actual SQL/broker dependencies. Preserve
stable recipient identity; do not promise exactly-once HTTP delivery.

Use bounded read-only test design/research when useful and one focused final
review. Keep fault controls in the test/sample harness. Distinguish emulator
limitations, sender knowledge and recipient effects. Add a short failure-and-
recovery demo using the existing telemetry viewer, with no custom frontend.

Verify affected behavior and final M2 acceptance. Update the CI workflow/tests
and record whether remote CI actually ran. Update README.md/docs/STATUS.md with
the implemented result and remaining limitations. Report what I should be able
to explain about the observed crash windows.

Leave changes uncommitted unless this session separately authorizes commits.
Do not push, publish, change external settings or provision paid resources.
