I authorize preparation of RelayLab M3: cloud deployment and operations.
Paid provisioning and external account/settings mutations are not yet approved.

Read AGENTS.md, docs/STATUS.md, PLAN.md and docs/OPERATIONS.md. Inspect the actual
M2 candidate and available environment. Prepare the Terraform resource set,
container build/deployment workflow, scoped identities/OIDC requirements,
authenticated ingress and recipient access, migration procedure, smoke test,
application revision rollback and teardown.

Complete all reversible local authoring and useful validation first. Consult
official sources for current Azure/provider/action behavior. Record genuine
validation results separately from prospective deployment steps. Use a bounded
read-only review for the candidate; do not expand into additional clouds or UI.

Then present one concrete deployment approval request containing the region,
resources/SKUs, current cost estimate for the planned duration, identity/settings
changes, exact apply/deploy/check/teardown steps and remaining risks. Explain
that the approval is needed for paid resources and external account changes.
Do not ask the owner to approve an unwritten configuration.

After this session explicitly approves that concrete deployment scope, execute
only the approved work, collect real pipeline/image/environment evidence,
demonstrate smoke/failure behavior and rollback, and perform approved teardown
or retention. Document actual outcomes. Do not infer approval from this file's
presence or from a Terraform plan.

Keep Git commits/pushes/publication separate unless the session authorizes them.
