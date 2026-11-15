@AGENTS.md

# Claude Code orchestration

## Integrator rules

- Delegate execution to Codex with file-based packets that name explicit file ownership and
  acceptance criteria.
- Re-run applicable gates fresh before integration; worker reports are evidence to inspect,
  not inherited verification.
- Use a different model family for review: Codex-authored work goes to a fresh Claude reviewer
  such as `.claude/agents/reviewer.md`, while Claude-authored work goes to Codex.
- Land one conventional commit per story on `main`, update BACKLOG, and keep README Status
  honest.
- Treat phone, Google Home, and physical-device evidence as human-provided; agents must not
  infer or invent it.

<!--
This file imports AGENTS.md (the shared playbook for every agent); Claude-only conventions
live here, area rules in .claude/rules, procedures in .claude/skills.
-->
