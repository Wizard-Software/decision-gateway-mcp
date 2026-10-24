# AGENTS.md

This file provides guidance to AI coding assistants (Codex, Gemini CLI, Copilot CLI, and other tools that follow the AGENTS.md convention) when working with code in this repository.

<!-- forge:conventions:start -->
## Forge conventions

This project uses [Forge](https://github.com/asawicki/Forge) SDLC orchestration skills.

**Project language:** `pl`  <!-- pl | en | ask — read by every Forge skill via language-loading.md; "ask" means prompt per-run -->

**Commit/PR language:** `en`  <!-- pl | en | ask | project — language for commit messages & PR titles/bodies; `project` inherits **Project language:** above; "ask" means prompt per-run -->

**Option analysis:** `off`  <!-- swot | brief | off — analysis printed before a choice question; read via user-interaction-rule.md; a missing line means that canon's default level -->

**Layout:**
- `.forge/context/` — user-provided context files Forge skills read as input (e.g., `glossary.md` with domain abbreviations). Safe to commit.
- `.forge/docs/` — Forge-generated artifacts: architecture specs, ADRs, PRD/TDD, Event Storming diagrams, task lists. Internal to Forge workflows.
- `.forge/orchestration/` — Orchestrator state files for pause/resume across sessions.
- `docs/` — User's public documentation (DocFX, etc.). Never touched by Forge.

**Glossary:** When analyzing client documents, Forge reads `.forge/context/glossary.md` to expand domain abbreviations (e.g. `ACME → Advanced Customer Metering Engine`). Run `/forge:init` to scaffold the template, then edit it with project-specific terms.

**Available Forge commands:** `/forge:init`, `/forge:task`, `/forge:gap`, `/forge:idea`, `/forge:enhance`, `/forge:arch-update`, `/forge:arch-spec`, `/forge:req-analysis`, `/forge:bug-fix`, `/forge:docs`, `/forge:release`.
<!-- forge:conventions:end -->
