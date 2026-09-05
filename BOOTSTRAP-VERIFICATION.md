# Bootstrap verification

Package: RelayLab Codex Astra bootstrap 1.0.0 · 2026-09-05.

This is the historical bootstrap-package record. Current implementation and execution evidence are in [docs/STATUS.md](docs/STATUS.md).

## Executed checks

- All four TOML files parsed successfully.
- All four configurations validated against the official Codex JSON Schema fetched for this package.
- The three role file references resolve; every role selects Astra and read-only sandboxing.
- Primary model/high reasoning, two-subagent cap and absence of primary permission overrides checked.
- Local Markdown file links and fenced-block balance checked.
- UTF-8/LF text, final newlines and absence of embedded host-specific paths checked.
- No product source/projects, compiled output or package dependencies included.
- Manual consistency review covered M1 versus M2 behavior, identity/acknowledgement boundaries, scope and authorization language.

Schema source: [official Codex schema](https://developers.openai.com/codex/config-schema.json).

## Verification limits

This validates a documentation/configuration bootstrap. Codex CLI/Desktop was not installed in the preparation environment. Effective Desktop configuration, model entitlement and role execution must be verified on the owner's host. There was no independent subagent review of this package.

No product restore, build, test, CI or deployment was performed. SDK/package/image versions will be selected against the real host during M1. Markdown rendering was not browser-tested; local link existence and fence structure were checked. External links are source references, not a claim of an exhaustive link-health scan.

The package is ready to start the bounded M1 task; it is not a completed application or an architecture proof.
