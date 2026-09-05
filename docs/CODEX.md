# Codex and GPT-6 Astra setup

Checked against official documentation during bootstrap preparation on 2026-09-05. Static package verification is described in BOOTSTRAP-VERIFICATION.md. The target Desktop client/account was unavailable in that preparation environment; actual M1 role execution and model-inspection limits are recorded in [STATUS.md](STATUS.md).

## Selected configuration

The project config requests model gpt-6-astra and high reasoning effort. High is a deliberate starting trade-off for this transaction/concurrency work, not a claim that it is universally the fastest or best setting. The researcher uses medium; test_designer and reviewer use high. All roles use Astra and are read-only. At most two subagents can be active alongside the primary agent.

The official [Astra guide](https://developers.openai.com/api/docs/guides/latest-model) documents the model ID and highlights sensitivity to ambiguous instructions, excessive verification and delegation guidance. This package addresses those behaviors with bounded tasks and explicit completion conditions. It does not change the model's capabilities or guarantee optimal output.

## Applying it in Desktop

Open the intended repository and trust it through the normal client flow if prompted. Project-scoped configuration is loaded only for trusted projects. Select GPT-6 Astra in the active thread and check its reasoning level; an existing thread or managed setting can override project defaults. Configuration cannot grant access to a model unavailable to the account.

The config leaves primary-agent sandbox/approval policy to the host. Read-only role configs are intentionally explicit. Do not enable full access, edit account credentials or loosen policy because a command was rejected.

Codex reads AGENTS.md as project guidance. Other documents are referenced by task, not dumped into every context. Restart/start a new project session after installing the configuration so it can load normally. Do not copy SourceGen Auditor's global rules into this project.

## Host verification

The first prompt asks the agent to identify the repository, active guidance, model selection when inspectable and registered roles. Do not treat its own claim as proof if the client does not expose effective model/configuration state; use the UI/config diagnostics.

If the CLI is installed, inspect its actual --help/configuration diagnostics before selecting a validation command. Do not assume an undocumented --strict-config switch exists in that installation. TOML parsing validates syntax; schema validation validates known structure; neither proves Desktop loaded the config or that the account can run Astra.

If an older client rejects a supported key, use the installed version's documented equivalent or update the client through its normal owner-controlled process. Preserve the model request, read-only roles and bounded delegation. Do not temporarily remove reviewer controls, change global policy or silently substitute another model. A role unavailable at runtime is disclosed; the primary agent can perform the focused review itself.

## Efficient use

Give one milestone prompt at a time. Keep normal corrections in the same task; ask for a short status when needed. Use prompts/04-RESUME.md after interruption. Keep docs/STATUS.md small and current.

Do not add persona stacks, repeated expert instructions, artificial thinking budgets, custom context-window values, temperature overrides, hook frameworks or MCP servers without a concrete need. No custom skill is required for this repository: the current instructions and three narrow roles cover the work. Add a reusable skill only after a repeated workflow justifies it.

Config sources: [reference](https://learn.chatgpt.com/docs/config-file/config-reference), [subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents), [AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md). Agent role paths resolve relative to .codex/config.toml. These files must remain valid even when no role has yet been executed.
