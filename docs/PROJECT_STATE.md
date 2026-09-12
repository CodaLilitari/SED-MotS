# SED MotS project checkpoint

Updated: 2026-09-12. This is the short recovery entry point. Read this file, then the relevant entries in [WORK_LOG.md](WORK_LOG.md), before resuming work after a context change. Verify Git and artifact state; a checkpoint describes an observed state, not a substitute for fresh inspection.

## Objective and current milestone

Build a modern Windows JKDF2/MotS editor with a shared human and agent authoring model. OpenJKDF2/OpenMotS is the primary engine target; preserve retail compatibility where supported and report independent evidence for it.

Completed milestone LIGHT-01: preserve independent native MotS source-light intent through import, shared editing, copy/undo, JKL save/reopen and independent surface-light calculation. Retain old single-intensity behavior. Do not claim a native-project writer: the current native path imports JED/SED/NED into a JKL document.

Completed acceptance record: [LIGHT-01](LIGHT-01.md). Implementation passed 908 unit tests and 71 headless UI checks. Self-contained Windows package `0.4.0-mots.7-dev` passed all four agent workflows (154 calls, 292 assertions) and 681-file distribution verification. Operative decisions are `DEC-20260912-01` through `DEC-20260912-05`. The acceptance record is authoritative rather than a competing copy of criteria inside the app goal.

## Current source and completed ownership

- Editor implementation/package commit: `f6100fe6f5cad1b9e7b57d3518bf2406996f4eaf`, branch `codex/mots-dotnet`. Completion documentation follows in a separate commit. Previous implementation base was `9d1d316`.
- Separate engine base: `b1be98bd4f170b5384cb71b15443a274aac6a937`. Engine code is not part of this milestone.
- Pre-existing tracked deletions: `.DS_Store`, `dotnet/.DS_Store`, `dotnet/src/.DS_Store`. Leave them outside milestone commits.
- Core model/editing/bake and tests: `model_hierarchy_preservation` subagent.
- Native/JKL formats and preservation tests: `transparency_contract` subagent.
- Agent acceptance tests and independent source audit: `arch_export_path` subagent.
- GUI/automation integration, documentation, review and final validation: root.

## Agreed contract

- `Light.Intensity` remains white/mono intensity and the shared fallback for legacy single-intensity sources.
- Nullable `RgbIntensity`: null inherits `Intensity`; an explicit zero or equal value remains explicit. `EffectiveRgbIntensity` resolves the fallback.
- Nullable `LegacyRgbRange` preserves legacy metadata. The inspected legacy baker uses the primary `Range` for both channels; do not introduce a second falloff rule.
- MotS baking uses the independent contributions; existing JK/Infernal Machine calculation behavior remains unchanged.
- Preserve ordinary JKL LIGHTS layouts. A versioned, strictly checked `SED_SOURCE_LIGHTS` section carries otherwise-lost authoring metadata, bound to the ordinary LIGHTS representation. Single-intensity files without extra metadata should not gain it gratuitously.
- GUI and CLI use shared edit commands. Agent reads and plan/readback receipts must expose stored versus effective values. Unrelated edits must preserve metadata.

## Acceptance evidence categories

1. Exact supported source-light values survive native import, JKL generation, reopen and unrelated edits. Explicit/null/zero/equal states remain distinct.
2. Independent channel calculation, old fallback behavior, metadata-only range behavior, clone/copy, and undo/redo pass targeted original fixtures.
3. Agent discovery, query, reviewed plan/apply, conversion, malformed metadata and stale-input refusal are exercised.
4. Full solution builds and appropriate regression tests pass; record exact commands, counts, receipts and source identity in the work log.
5. Human GUI controls compile and preserve precision. UI/runtime validation must be named separately from source review and build success.
6. Package/release status is explicit. A source fix is not a rebuilt distribution.

## Subsequent milestones

| Milestone | Acceptance target | State |
|---|---|---|
| Spatial and reference-safe editing | Build and revise a six-to-ten-sector outpost without serialized-index arithmetic; ambiguous/stale selection refuses | Planned |
| Full architectural lighting authoring | Separate bind/rebind/rebake; dependencies and stale-bake status; undo samples and provenance | Planned |
| Controlled engine verification | Defined initialized state, frozen renders and bounded stepping; actual resource resolution and effective camera recorded | Planned |
| Human and agent playable workflow | Panel, door/lift, pickup and exit; build/play/revise/reopen via both interfaces | Planned |
| Thin MCP adapter | Shared commands, typed discovery/errors/images and explicit identities; compare against CLI on the same task | Planned after domain prerequisites |

## Existing evidence and boundaries

The previous Windows package is `0.4.0-mots.6-dev` at editor base `9d1d316`. Its historical evidence includes 801 unit tests, 111 packaged lit/resource assertions across 34 CLI calls, and 39 stock MotS levels / 2,353 ARCH bindings. The separate engine harness passed 1,185 checks. These counts belong to the previous checkpoint; the LIGHT-01 results above identify the new evidence. The stock/community corpus was not rerun for this milestone.

One nominally matched Rebel Base editor/OpenJK image pair measured 1.211/255 mean absolute RGB difference and 97.59% of pixels within eight values in every channel. This is one static preview versus one timed OpenJK frame, not retail or gameplay parity. Hierarchy pivots, lower-LOD drawing, complete sky/depth/material behavior and deterministic simulation remain separate gaps.

Keep original stock/community corpus inputs separate from WBW conversions. Keep game assets and private consultation transcripts out of source distributions. The local consultation synthesis and sent briefing are in `build/lily-consultation-20260912/`; the work log records technical decisions without depending on access to that conversation.

The current Windows/source handoff is `build/packages/SED-MotS-0.4.0-mots.7-dev-f6100fe/`; use its `verification.json`, workflow receipts and `SHA256SUMS.txt`. Package source, binaries and history all identify `f6100fe`. See `DEC-20260912-05` for hashes and gate counts. No agents have outstanding implementation ownership for LIGHT-01.

## Recovery discipline

Check `git status` and active agent ownership before edits. Read the latest work-log entries and linked receipts. Distinguish proposed, implemented, tested and packaged states. If a prior assertion lacks evidence, retain it as unresolved rather than reconstructing a result. Update this checkpoint at meaningful transitions and append corrections to the work log instead of silently rewriting earlier findings.
