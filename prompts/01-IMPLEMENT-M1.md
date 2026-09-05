We are starting RelayLab in this repository using GPT-6 Astra.

Implement M1 only. Read AGENTS.md, PRODUCT.md, ARCHITECTURE.md, PLAN.md,
docs/CONTRACTS.md and docs/STATUS.md. Use the task-specific reading map for
testing, presentation and CI. The bootstrap documents are the current project
baseline and replace the earlier standalone RelayLab brief/prompt.

Inspect the real repository state, installed SDKs, Docker access and applicable
Codex configuration first. Preserve unrelated existing files and any license.
Confirm Astra/model selection and the named read-only roles when the client
exposes them; report what cannot be inspected. Do not silently change models.

Give a short implementation plan, then proceed without waiting for approval of
routine code, package or test choices. Resolve compatible versions from the
actual environment and official sources. Identify concrete blockers early;
complete independent authorized work before asking a focused question.

Build the local API/outbox/Service Bus/worker/receiver slice, the status API,
meaningful SQL and broker integration tests, a documented demo, and an authored
GitHub Actions CI workflow. Follow M1's single-normal-HTTP-attempt policy; full
scheduled retries, replay and telemetry experiments belong to M2.

Use the configured test_designer for a bounded read-only acceptance design task
when it can run alongside implementation. Use researcher only for a specific
unresolved official-source question. The primary agent owns all file writes and
execution. Use reviewer on the final M1 candidate; follow AGENTS.md's disclosed
fallback if the client cannot launch roles. No recursive delegation.

Run the M1 acceptance, fix concrete failures and perform focused review. Create
actual verification commands after choosing the runner; do not invent a success
from prospective commands. Include intended untracked source in fresh-input
verification. Remote CI remains unverified until an authorized push/run occurs.

Update README.md with verified usage and docs/STATUS.md with actual environment,
commands, results and remaining work. End with a concise implementation summary,
verification evidence, limitations and three questions I should be able to
explain at interview.

Leave work uncommitted. Do not push, publish, alter external settings, provision
cloud resources or advance to M2/M3. The authorized outcome is a reviewable M1.
