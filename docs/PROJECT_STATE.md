# SED MotS project checkpoint

Updated: 2026-09-20. This is the short recovery entry point. Read this file, then the relevant entries in [WORK_LOG.md](WORK_LOG.md), before resuming work after a context change. Verify Git and artifact state; a checkpoint describes an observed state, not a substitute for fresh inspection.

Current workspace: `C:\Users\Andrei\Documents\ChatGPT\SED-MotS-Net`.
The incidental `openmots` checkout is a separate engine project.

**Active milestone: [SPATIAL-01](SPATIAL_SELECTION.md).** Shared spatial selection
and source/geometry-bound operation targets are implemented. The integrated
build passes 1,043 unit tests and 91 actual headless UI checks. The executable
Relay Outpost acceptance passes 52 calls/458 checks, with both diagnostic views
inspected. Replacement packaging and publication are pending.
Start from `DEC-20260920-08` and subsequent entries for the current work.

**Current gate (2026-09-20): GEO-01 complete, packaged and source published.**
Implementation/package commit `86428871e5b30cdbe4806c86efbfa77d7b01a6b2`
passes 981 unit tests, 91 actual headless UI checks and all four self-contained
agent workflows (161 calls/313 checks). It fixes independently reproduced
numeric colormap, textured UV, residual-row, source-baseline and writer-side-effect
defects, plus the resulting inspector ambiguity exception. The earlier `9a1f2ce`
package remains a superseded local candidate. See `DEC-20260920-04` through
`DEC-20260920-07`; package identity is distinct from this completion-only update.

Hosting: the public source is
[CodaLilitari/SED-MotS](https://github.com/CodaLilitari/SED-MotS), on `main`,
forked from `davidagnome/SED` in the `smlu/SED` network. See
[GITHUB_HANDOFF.md](GITHUB_HANDOFF.md). On 2026-09-20, the authenticated CLI
account and ADMIN permission were verified as CodaLilitari, with main the only
remote branch at the account-handoff base `65e94af` before this milestone.
The tested implementation was pushed and remote main verified at `8642887`;
this checkpoint update follows separately. No GitHub binary release was created.

## Objective and current milestone

Build a modern Windows JKDF2/MotS editor with a shared human and agent authoring model. OpenJKDF2/OpenMotS is the primary engine target; preserve retail compatibility where supported and report independent evidence for it.

Completed milestone LIGHT-01: preserve independent native MotS source-light intent through import, shared editing, copy/undo, JKL save/reopen and independent surface-light calculation. Retain old single-intensity behavior. Do not claim a native-project writer: the current native path imports JED/SED/NED into a JKL document.

Completed acceptance record: [LIGHT-01](LIGHT-01.md). Implementation passed 908 unit tests and 71 headless UI checks. Self-contained Windows package `0.4.0-mots.7-dev` passed all four agent workflows (154 calls, 292 assertions) and 681-file distribution verification. Operative decisions are `DEC-20260912-01` through `DEC-20260912-05`. The acceptance record is authoritative rather than a competing copy of criteria inside the app goal.

Completed bounded milestone: [GEO-01 geometry export preservation](GEOMETRY_REFERENCE_PRESERVATION.md).
The writer now preserves supported imported geometry slots or refuses export,
and geometry authoring accepts validated independent source-light metadata.
The full solution builds with zero warnings/errors and 981 unit tests pass.
The headless UI probe passes 91 checks, including 20 sector-colormap checks.
The replacement self-contained Windows package `0.4.0-mots.8-dev` at `8642887`
passes all four workflows (161 calls/313 assertions), separate smoke and 682-file
distribution/source/history verification. Its design workflow contributes 69
calls/129 assertions and both diagnostic images match the inspected versions.
Handoff: `build/packages/SED-MotS-0.4.0-mots.8-dev-8642887/`.
These results remain separate from LIGHT-01 and the superseded local candidate.

## Current source and completed ownership

- Current editor branch: `main`; final GEO-01 implementation/package is `86428871e5b30cdbe4806c86efbfa77d7b01a6b2`, after candidate `9a1f2ce3ac6eebef1be970fc7cd65b67e9f248bc` and hosting checkpoint `65e94af`. This completion documentation is a later commit.
- Previous LIGHT-01 implementation/package commit: `f6100fe6f5cad1b9e7b57d3518bf2406996f4eaf`; completion documentation was `2a1461a`. Its historical branch name was `codex/mots-dotnet`.
- Separate engine base: `b1be98bd4f170b5384cb71b15443a274aac6a937`. Engine code is not part of this milestone.
- Pre-existing tracked deletions: `.DS_Store`, `dotnet/.DS_Store`, `dotnet/src/.DS_Store`. Leave them outside milestone commits.
- SPATIAL-01 ownership: `sed_spatial_audit` owns Core selectors/digests and original tests; `sed_workflow_audit` owns selection protocol/Plan/schema and tests; `sed_export_review` owns the executable Relay Outpost workflow. Core and format implementations are frozen after integrated tests; executable acceptance is pending.
- Root owns process-level workflow, review, documentation, packaging and publication. No worker commits or pushes independently.

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

GEO-01 is the bounded prerequisite to the spatial-authoring milestone below.
Fresh regression tests reproduced its failures before production fixes. Source,
executable and replacement-package checks pass. Spatial selectors remain planned rather
than implied by the geometric preservation repair.

| Milestone | Acceptance target | State |
|---|---|---|
| [Spatial selection and authoring (SPATIAL-01)](SPATIAL_SELECTION.md) | Build and revise an eight-sector outpost without serialized-index arithmetic; ambiguous/stale selection refuses | Implementation in progress; acceptance not yet run |
| Full architectural lighting authoring | Separate bind/rebind/rebake; dependencies and stale-bake status; undo samples and provenance | Planned |
| Controlled engine verification | Defined initialized state, frozen renders and bounded stepping; actual resource resolution and effective camera recorded | Planned |
| Human and agent playable workflow | Panel, door/lift, pickup and exit; build/play/revise/reopen via both interfaces | Planned |
| Thin MCP adapter | Shared commands, typed discovery/errors/images and explicit identities; compare against CLI on the same task | Planned after domain prerequisites |

## Existing evidence and boundaries

The previous Windows package is `0.4.0-mots.6-dev` at editor base `9d1d316`. Its historical evidence includes 801 unit tests, 111 packaged lit/resource assertions across 34 CLI calls, and 39 stock MotS levels / 2,353 ARCH bindings. The separate engine harness passed 1,185 checks. These counts belong to the previous checkpoint; the LIGHT-01 results above identify the new evidence. The stock/community corpus was not rerun for this milestone.

One nominally matched Rebel Base editor/OpenJK image pair measured 1.211/255 mean absolute RGB difference and 97.59% of pixels within eight values in every channel. This is one static preview versus one timed OpenJK frame, not retail or gameplay parity. Hierarchy pivots, lower-LOD drawing, complete sky/depth/material behavior and deterministic simulation remain separate gaps.

Keep original stock/community corpus inputs separate from WBW conversions. Keep game assets and private consultation transcripts out of source distributions. The local consultation synthesis and sent briefing are in `build/lily-consultation-20260912/`; the work log records technical decisions without depending on access to that conversation.

The current verified Windows/source handoff is `build/packages/SED-MotS-0.4.0-mots.8-dev-8642887/`; use its `verification.json`, workflow receipts and `SHA256SUMS.txt`. Package source, binaries and history all identify `8642887`. See `DEC-20260920-07` for hashes and gate counts. The earlier `9a1f2ce` candidate is superseded. GEO-01 executable receipts and negative controls remain under `build/geometry-preservation-20260920/`. The prior `.7` handoff is retained separately.

## Recovery discipline

Check `git status` and active agent ownership before edits. Read the latest work-log entries and linked receipts. Distinguish proposed, implemented, tested and packaged states. If a prior assertion lacks evidence, retain it as unresolved rather than reconstructing a result. Update this checkpoint at meaningful transitions and append corrections to the work log instead of silently rewriting earlier findings.
