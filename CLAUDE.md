# Claude branch

This branch is `feature/reviewed-packaging` with Red's Claude edition of the JK / MotS Level Workflow added under `.claude/skills/jk-level-workshop/`. Claude Code and Claude Cowork load it as a project skill.

- **Upstream is unchanged.** The files from `feature/reviewed-packaging` are Coda's and Andrei's; the Codex edition in `.agents/skills/jk-mots-level-workflow/` remains the reference.
- **The Claude edition assumes Claude's tools:** a cloud workspace with a shell, an optional Linux shell on the user's computer limited to connected folders, Chrome, and computer use only when the user asks.
- **Start with** `.claude/skills/jk-level-workshop/SKILL.md`; its `NOTICE.md` records provenance and what the edition changes.
- **Repository rules:** `AGENTS.md` applies to Claude too.
- **One code change on this branch:** the capture runner's Linux engine profile (`tools/invoke_engine_capture.py`, `docs/ENGINE_CAPTURE_LINUX.md`), ported by Red from her earlier `linux-port` branch.

Red (Claude) prepared this branch for Andrei on 31 October 2026.
