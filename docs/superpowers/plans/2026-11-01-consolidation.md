# SED-MotS consolidation implementation plan

**Goal:** Consolidate existing Linux contributions and bring Windows to the same
shared authoring, compatibility and packaged-tool feature set.

**Architecture:** Keep the existing GUI, CLI and MCP interfaces and one .NET
document authority while introducing a pinned, stateless C++ core inspection
worker. Use actual upstream codecs rather than duplicating them in C#. Larger
mutation/document/Qt adoption remains gated by the
[reviewed decision](../../audit/CPP_ADOPTION_DECISION_20261101.md).

**Spec:** [Consolidation design](../specs/2026-11-01-consolidation-design.md).

- [x] Recover checkpoint, refresh hosted refs, compare histories and tree identity.
- [x] Create an isolated consolidation worktree and preserve both Red histories.
- [x] Reproduce legacy final-frame edit/save failures with focused regression
      fixtures; fix shared serialization and check original-level regeneration
      without widening arbitrary malformed input.
- [ ] Add Windows package support/credit parity, external build paths, bounded
      processes and dependency-package acceptance.
- [x] Update the upstream/Holocron/editor comparison and reconcile stale queues.
- [ ] Implement and qualify the readonly C++ inspection worker and host adapter;
      package corresponding pinned source and test Windows/Linux behavior.
- [ ] Run Core/MCP and relevant Python suites; inspect source diffs and receipts.
- [ ] Build and verify Linux and Windows distributions from identified source.
- [ ] Derive Windows parity branch; record exact ancestry, validation and limits.

Review focus: malformed final-only frame plus added properties; loss of source
notices or support tools in Windows archives; misleading static dependency
closure claims; source/generated-output separation; stale checkpoint items
mistaken for current unimplemented defects. No contact with Red is authorized.
