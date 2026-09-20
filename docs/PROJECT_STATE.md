# SED MotS project checkpoint

Updated: 2026-09-20. This is the short recovery entry point. Read this file, then the relevant entries in [WORK_LOG.md](WORK_LOG.md), before resuming work after a context change. Verify Git and artifact state; a checkpoint describes an observed state, not a substitute for fresh inspection.

Current workspace: `C:\Users\Andrei\Documents\ChatGPT\SED-MotS-Net`.
The incidental `openmots` checkout is a separate engine project.

**Latest completed bounded milestone: [ENGINE-01](ENGINE_CAPTURE.md).**
Implementation/package source is `ea4d6f4a8c39501198064a52264317b16e5741e0`,
Windows version `0.4.0-mots.11-dev`. Original agent-authored MotS architecture
passed 21 hidden runtime captures: seven scenarios across three clean-process
repeats, 33 image checks and both refusal controls. Geometry-defined panel and
world coverage were each 100%. Observed repeated frames match exactly; this
does not make the engine's wall-time simulation deterministic.

The final clean package passed 1,098 units with zero build warnings/errors,
all five agent workflows (229 calls/799 checks), smoke and exact verification
of 691 distribution files, matching source and full history. Python acceptance
tools pass 75 tests. Final packaged fixture preparation made 43 agent calls;
all 56 staged files across eight scenarios match the matrix fixture byte for
byte, and all five packaged companion tools match the matrix-tested source.
A fresh packaged single-case capture also passed. Current milestone did not
rerun the previous 113-check headless UI probe; no C# UI code changed.

Engine main `74e4657c` already contains the material-row fix and has identical
production source/build inputs to integrated capture `197c2625`. Its 2,603
committed source payload files and runtime binary identities were freshly
verified. Execution used existing executable `4f9c4f7a...`, whose historical
build used a copied dependency cache. Matrix-01 on the stale executable remains
superseded, including its original passing receipt and later failing coverage
check. A duplicate engine experiment remains local research, outside publication.

Lilitari (ChatGPT 5.6 Pro) and Grok Build (`grok-4.6-build`, requested xhigh)
provided review input; adopted findings were independently checked. Limited
world/player state is engine-reported. Effective camera, resolved resource bytes,
loaded ARCH arrays, engine mono-mode crossover, retail parity and gameplay remain
unverified. See DEC-15 through DEC-19 and `build/engine01-20260920/`.

**User stop condition:** publish this tested revision and completion checkpoint,
then pause. No next milestone or separate engine research is authorized to
continue automatically after that publication.

**Previous completed milestone: [ARCH-01](ARCHITECTURE_AUTHORING.md), packaged and
source published.** Implementation/package source is
`adb0caeab1f6a4f4f089cd1df94bd7dcc2ce9441`. Shared bind, reset and unoccluded
source-point baking, saved provenance, computed stale status, paginated samples,
actual inspector controls and raw model identity in the renderer are implemented.
Source and clean package builds each pass 1,098 units with zero warnings/errors;
the corrected actual headless UI probe passes 113 checks. Five packaged workflows
pass 229 calls/799 checks, with 685 distribution members and matching source/full
history verified. The three inspected interior before/RGB/mono images match the
packaged images exactly. See DEC-20260920-12 through 14. This completion
documentation follows the tested implementation in a separate commit.

**Previous milestone: [SPATIAL-01](SPATIAL_SELECTION.md), packaged and
source published.** Implementation/package source is
`1fe296a65f331161e13a4dac20f3306d322d3b53`. The integrated build passes 1,043
unit tests and 91 actual headless UI checks. The self-contained Relay Outpost
acceptance passes 52 calls/458 checks; all five packaged workflows total 213
calls/771 checks. Both diagnostic views were inspected and match the packaged
images exactly. See `DEC-20260920-08` through `DEC-20260920-11`; this completion
documentation follows the tested source in a separate commit.

**Previous milestone (2026-09-20): GEO-01 complete, packaged and source published.**
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
remote branch before and after this milestone.
Remote main was verified at `bcba14a` before ENGINE-01 publication. Its tested
implementation/package is `ea4d6f4`; this completion-only checkpoint follows in
a separate commit. The final push receipt records the resulting remote identity.
No GitHub binary release was created; the verified Windows ZIP is a local handoff.

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

Completed [SPATIAL-01](SPATIAL_SELECTION.md): select convex rooms by world point
and faces by outward direction, then use exact source/geometry-bound tokens in
supported shared editing commands. The eight-room Relay Outpost exercises seven
extrusions, two additional adjoins, metadata revisions and deliberate refusal
cases. Windows package `0.4.0-mots.9-dev` at `1fe296a` passed all five workflows,
smoke and verification of 684 distribution files plus matching source/history.

Completed bounded [ARCH-01](ARCHITECTURE_AUTHORING.md): explicit current-resource
bind, reset/rebind, and `source-points-v1` baking with private allocations and exact
undo. The saved `SED_ARCH_AUTHORING` extension records origin, raw model identity,
input and sample digests; local freshness and resource validation remain separate.
Single-LOD rest-pose sampling is unoccluded and non-directional. Unsupported pivots,
insertion offsets and ambiguous hierarchy instances are refused. This is not a
shadowed or retail-equivalent baker. Windows `.11-dev` retains this authoring path
and adds the separate bounded runtime capture workflow.

## Current source and completed ownership

- Current editor branch: `main`; ENGINE-01 implementation/package is `ea4d6f4a8c39501198064a52264317b16e5741e0`. This completion documentation is a later commit. Previous ARCH-01 implementation/package is `adb0caeab1f6a4f4f089cd1df94bd7dcc2ce9441`.
- Previous SPATIAL-01 implementation/package is `1fe296a65f331161e13a4dac20f3306d322d3b53`; its completion documentation was `896c808`.
- Previous GEO-01 implementation/package is `86428871e5b30cdbe4806c86efbfa77d7b01a6b2`, after candidate `9a1f2ce3ac6eebef1be970fc7cd65b67e9f248bc` and hosting checkpoint `65e94af`.
- Previous LIGHT-01 implementation/package commit: `f6100fe6f5cad1b9e7b57d3518bf2406996f4eaf`; completion documentation was `2a1461a`. Its historical branch name was `codex/mots-dotnet`.
- Separate engine target: published `74e4657c4f1751d09af9ad9e056acb8f41997acb`, production-source equivalent to capture `197c26255883fadbc7b90dcd3a2573572696058f`. Local `b1be98bd` and its original capture executable were stale. No engine change is published in this editor milestone.
- Pre-existing tracked deletions: `.DS_Store`, `dotnet/.DS_Store`, `dotnet/src/.DS_Store`. Leave them outside milestone commits.
- ARCH-01 completed ownership: `sed_spatial_audit` owns Core authoring/digests/commands and independent format review; `sed_workflow_audit` owns resource/model preparation, extension and format tests; `sed_export_review` owns inspector controls, actual headless acceptance, Core tests and renderer regression. All implementations are frozen and integrated gates passed.
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

GEO-01 preservation and the bounded SPATIAL-01 selection/authoring workflow are
complete. Tokens are source-bound consistency checks, not persistent editor IDs;
general remapping, native output and broader game behavior remain separate.

| Milestone | Acceptance target | State |
|---|---|---|
| [Spatial selection and authoring (SPATIAL-01)](SPATIAL_SELECTION.md) | Build and revise an eight-sector outpost without serialized-index arithmetic; ambiguous/stale selection refuses | Complete within the documented convex-geometry/token scope |
| [Architectural lighting authoring (ARCH-01)](ARCHITECTURE_AUTHORING.md) | Bind/reset/bake with captured resources, exact undo, saved provenance and stale readback | Complete within the unoccluded single-LOD recipe; shadowed baking and LOD correspondence remain separate |
| [Static runtime RGB verification (ENGINE-01)](ENGINE_CAPTURE.md) | Original authored fixture, independent coverage/channel controls, metadata invariants, repeated captures and bounded refusal controls | Complete within the documented static RGB scope; simulation is wall-time and only limited world/player state is observed |
| Instrumented engine verification | Defined initialized state, frozen renders and bounded stepping; actual resource resolution and effective camera recorded | Future work, paused |
| Human and agent playable workflow | Panel, door/lift, pickup and exit; build/play/revise/reopen via both interfaces | Planned |
| Thin MCP adapter | Shared commands, typed discovery/errors/images and explicit identities; compare against CLI on the same task | Planned after domain prerequisites |

## Existing evidence and boundaries

The previous Windows package is `0.4.0-mots.6-dev` at editor base `9d1d316`. Its historical evidence includes 801 unit tests, 111 packaged lit/resource assertions across 34 CLI calls, and 39 stock MotS levels / 2,353 ARCH bindings. The separate engine harness passed 1,185 checks. These counts belong to the previous checkpoint; the LIGHT-01 results above identify the new evidence. The stock/community corpus was not rerun for this milestone.

One nominally matched Rebel Base editor/OpenJK image pair measured 1.211/255 mean absolute RGB difference and 97.59% of pixels within eight values in every channel. This is one static preview versus one timed OpenJK frame, not retail or gameplay parity. Hierarchy pivots, lower-LOD drawing, complete sky/depth/material behavior and deterministic simulation remain separate gaps.

Keep original stock/community corpus inputs separate from WBW conversions. Keep game assets and private consultation transcripts out of source distributions. The local consultation synthesis and sent briefing are in `build/lily-consultation-20260912/`; the work log records technical decisions without depending on access to that conversation.

The current verified Windows/source handoff is `build/packages/SED-MotS-0.4.0-mots.11-dev-ea4d6f4/`; use its `verification.json`, five workflow receipts and `SHA256SUMS.txt`. Package source, binaries and complete history identify `ea4d6f4`; its embedded checkpoint describes the build-time state, while this later completion checkpoint records the executed final gates. See `DEC-20260920-19` for hashes and counts. Its `agent-lit-snapshot/architecture-baked.jkl` and `architecture-stale.jkl` demonstrate authoring/readback; `agent-spatial/relay-final.jkl` remains the original eight-room structural fixture. Prior packages remain separate, including the superseded `.11-dev-ed17c14` capture-tool candidate. Current runtime evidence is in `build/engine01-20260920/matrix-02/`, packaged fixture preparation in `fixture-07/`, and the final packaged diagnostic in `diagnostic-08/`. Stock bootstrap assets and the separate engine are not included in the editor package. This is bounded static engine evidence, not retail parity or a playable-level claim.

## Recovery discipline

Check `git status` and active agent ownership before edits. Read the latest work-log entries and linked receipts. Distinguish proposed, implemented, tested and packaged states. If a prior assertion lacks evidence, retain it as unresolved rather than reconstructing a result. Update this checkpoint at meaningful transitions and append corrections to the work log instead of silently rewriting earlier findings.
