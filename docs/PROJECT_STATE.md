# SED MotS project checkpoint

Updated: 2026-09-30 (TOOL-01 first slice locally packaged and independently audited). This is the short recovery entry point. Read this file, then the relevant entries in [WORK_LOG.md](WORK_LOG.md), before resuming work after a context change. Verify Git and artifact state; a checkpoint describes an observed state, not a substitute for fresh inspection.

Current workspace: `C:\Users\Andrei\Documents\ChatGPT\SED-MotS-Net`.
The incidental `openmots` checkout is a separate engine project.

**TOOL-01 shared editing workbench, first slice implemented.** Andrei approved proceeding after
the plug-in audit. The first implementation slice adds exact query predicates,
selection-wide UV projection/transforms, deterministic bounded new thing/light
arrays, GUI dialogs and the corresponding existing CLI/MCP contracts. The final
clean Release suite passes **1,517 Core + 270 MCP** tests; headless GUI probes
pass **149** checks. The actual self-contained package passes the strengthened
TOOL-01 workflow (**132 CLI calls, 1,099 checks, 14 expected refusals**) and
**63 MCP stdio calls**. Existing original authoring checks also pass. These are
authoring/serialization results, not new engine or retail gameplay proof.

**Latest local Windows package:** `0.4.0-mots.15-dev`, built from exact commit
`f85aec0deda71bff59435b061aed8a7fad4ff53c`, at
`build/packages/SED-MotS-0.4.0-mots.15-dev-f85aec0/`.
The Windows ZIP SHA256 is
`58dc1ed7cac640957946fccf5ff3bb4988afd46188b744628f7c83be0187b71f`.
Matching `SED-MotS-source.zip`, `SED-MotS-history.bundle` and `SHA256SUMS.txt`
are alongside it. The independent audit passes 934 manifested/935 physical
files, exact ZIP payload, all 818 source Git blobs and all 818 compiler-input
files, plus a standalone 354-commit/3,807-object history with strict fsck.
It independently recomputed 44 complete thing/light rows, UV math, vertex
preservation, query results and positive/refused COG-reference cases. Evidence:
`build/tool01-20260930/package-audit-01/audit.json`, SHA256
`ab1e11b6780419fc006de0467be6672b77b77b73fbff809d4b3b38ec733018dc`.
Source follow-up commits which only update this checkpoint are not substituted
for the frozen package identity. Nothing was pushed or uploaded.

**Next implementation scope:** masked property batches and report-first repair
helpers, then reference-safe prefabs/connected stairs and gameplay recipes.
Material/model-aware lighting remains separate. Preserve the two failed package
attempts: one exposed checkout EOL-dependent original fixtures; the other an
old MCP fixture assumption invalidated by correct empty-room capacity saving.
Both are corrected and the final clean package passes. See DEC-20260930-02.

All implementation is original; the source-informed audit is not described as
a formal clean-room process. PLAY-01's separate pending review/release remains
paused; no older package or publication receipt is replaced. See
[TOOL_WORKBENCH.md](TOOL_WORKBENCH.md) for the scope and acceptance contract.

**Plug-in research completed, 2026-09-30:** Andrei requested a broad JED/SED
plug-in functionality survey. This resumed that research scope only. The
[capability audit](audit/JED_SED_PLUGIN_AUDIT_20260930.md) and
[structured catalog](audit/JED_SED_PLUGIN_CATALOG_20260930.json) cover 45 legacy
plug-in/companion families, three adjacent importer families, 25 pinned public
repositories and public forum discoveries. Proposed TOOL-01 adds shared
selection/UV/placement/prefab/gameplay recipes; material/model-aware lighting
remains a separate proposed milestone. No implementation, downloaded-binary
execution, release/reviewer polling or publication occurred. The existing
PLAY-01 handoff below remains paused. Original reports, downloads, raw public
forum pages and hash receipts are external at
`C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\plugin-audit-20260930\`.
Repository documents contain original summaries and public links only, without
raw Discord logs or copied forum posts. See DEC-20260930-01 for coverage,
source-license distinctions, independent corrections and actual validation.

**PLAY-01 handoff PAUSED at Andrei's request, 2026-09-28 11:36 UTC.** Stop its
publication and review polling until he resumes that handoff. The tested local handoff below
is the stopping point. No PLAY-01 source push, release creation or asset upload
has occurred. The previously published `.13-dev` remains unchanged.
Final Lilitari correction review was delivered once at 11:32:32 UTC in the saved
SED conversation and was still running at the last observation; leave it
uninterrupted. Exact job/message/attachment identities and resume instructions
are ignored/local in `build/play01-20260928/reviews/lilitari-corrections-01/`.
On resume, inspect that existing reply before any retry, triage findings, then
use the independently audited nine-asset selection in `release-upload-02/`.
The older `release-upload/` has an explicit publication hold and is superseded.

**Latest PLAY-01 handoff candidate:** corrected editor source/package
`00e7741990aa2b3e48e938136da0a383c5241782`,
`build/packages/SED-MotS-0.4.0-mots.14-dev-00e7741/`. Its independent audit passes
930 manifested files, all 794 source files, and exact 350-commit/3,731-object
history. Clean Release tests pass 1,376 Core + 269 MCP; Python passes 115.
The clean native companion is `d18ecc2a931586d5e57237cb0d301c9d07b89336`,
candidate04 EXE SHA256 `72170044bcd951c80c48adc434c879e41d18aab1e53c39280c9aafdf27f401d0`.
Actual packaged `runtime-packaged-02` passes three positive and four fault-control
processes with the stronger oracle (maximum observed state gap 50 ms). The v2
independent audit also passes, including primary-verdict gates and explicit
staged EXE/OpenAL/GNS coverage; freeglut is separately packaged.
Final focused source review and publication remain pending while paused. Earlier candidate
paragraphs below are retained as development history, not upload selections.

**Active milestone: [PLAY-01](PLAYABLE_AUTHORING.md), 2026-09-28.** Andrei approved
the proposed small playable MotS workflow. Shared `addCog` and `setThingFrames`
are implemented locally. Original authoring/reopen passed in `fixture-04/`,
and `build/play01-20260928/runtime-05/acceptance.json` passed three positive and
four discriminating fault-control processes using the separate engine candidate
`engine-build-02/runtime/openjkdf2-64.exe` (SHA256 `f85d015451ea805f52a648440efd65c227023ace9dfd1420fe02c1b1ab7c4879`).
A separate checker independently audited all seven raw traces. The engine source
is in `C:\Users\Andrei\.codex\worktrees\play01-runtime\openmots`, base `74e4657c`
plus the recorded PLAY diff. Real-time normal inputs and physical/COG/inventory
observations establish this original scenario, not retail or campaign parity.
Lilitari's completed editor review exposed existing-reference capture, exact
in-memory undo numbering and missing final mover eligibility. Corrections pass
1,376 Core + 269 MCP tests. `fixture-05` completed 139 real stdio calls and
verifies the exact reciprocal four-room chain after save/reopen; its level/asset
bytes match the runtime-tested fixture exactly. Python tools now pass 115 tests.
Independent oracle review found and reproduced simulation-clock inflation of the
mover speed allowance. The corrected wall-only motion bound rejects both clock
counterexamples while all seven real traces retain their expected outcomes.
The completed native source review independently exposed additional oracle
false positives: early gate crossing, a skipped phase-1 witness, unconsumed
traversal controls and an activation edge during idle. These were reproduced
against the clock-corrected code. The corrected Python oracle preserves the
unchanged saved baseline and rejects each unchanged attack on its intended
predicate. All seven prior actual traces retain their expected results under
offline revalidation. Native COG-identity hardening and fresh runtime evidence
remain in progress; candidate03's first diagnostic exposed an incorrect
count-versus-highest-index assumption and is preserved as a failed attempt.
The focused Claude editor review reached its 1,200-second deadline without an
answer; delivery remains uncertain and no approval or retry is claimed.
The first clean package at `370a881` passed its checks but is superseded by the
oracle correction. Corrected source/package `6533ff826b95f6fece1425595ea4de105aaba001`
is prepared at `build/packages/SED-MotS-0.4.0-mots.14-dev-6533ff8/`; its independent
audit passed 930 manifested files, all 794 source Git blobs and complete history.
`runtime-packaged-01` then passed all seven actual engine processes using the
shipped Python tools and packaged authoring output; a second checker independently
verified all raw traces, staged hashes and expected outcomes (maximum gap 48 ms).
The native companion and exact source snapshot are in `engine-package-01/`;
all 2,611 tracked engine files map to new local commit `fec82961` without a rebuild.
The original example ZIP is independently verified in `example-handoff-01/`.
Nine release assets and notes were prepared in `release-upload/`, not uploaded.
They are now provisional: the native review findings require new source freezes,
immutable packages and acceptance evidence before normal pushes/publication.
Preserve the previous packages/audits; never substitute their hashes silently.
No new source publication or release is claimed yet. Current published package
remains MCP-01 below. Unrelated `.DS_Store` deletions stay excluded.

**MCP-01 completed handoff, 2026-09-27:** source/package commit
`ce093bdeb2c568b3717a1649775af9721e0a0ddc`, Windows `0.4.0-mots.13-dev`.
Clean build passed 1,329 Core + 265 MCP tests; six packaged CLI workflows passed
272 calls/866 checks; real packaged stdio MCP passed 65 completed calls and two
interrupted-write probes, including diagnostic and lit inline images. Exact
verification matched 924 distribution files with corresponding source/history.
Handoff: `build/packages/SED-MotS-0.4.0-mots.13-dev-ce093bd/`.
Independent package audit passed, including all 783 source Git blobs and exact
complete history: 345 commits and 3,647 reachable objects. Lilitari's final
changed-source review accepted the bounded contract and found two residual
guidance/documentation issues. Both are corrected in this final package,
including a preview cancellation regression. The initial `1b784ae` package is
preserved as a superseded candidate.
Normal push succeeded and remote main is verified at `ce093bd`. GitHub metadata is PUBLIC but
anonymous fetch returns 404. Use exact source attachments for review and record
actual publication status without bypassing the reported account restriction.

**Binary release published, 2026-09-27:**
[v0.4.0-mots.13-dev](https://github.com/CodaLilitari/SED-MotS/releases/tag/v0.4.0-mots.13-dev)
is a published prerelease tagged at the exact package source `ce093bd`.
All four uploaded assets match local sizes and SHA256 hashes: Windows ZIP,
source ZIP, history bundle and checksums. Anonymous release/asset requests still
return 404. See DEC-20260927-04 and local `build/mcp01-20260927/release-upload/`.
The earlier no-binary-upload statements describe their historical checkpoints.

**Latest completed milestone: [MCP-01](MCP_INTEGRATION.md), started 2026-09-25 from
`ef53c33`.** Andrei accepted the independently reviewed MCP recommendation and
asked for Lilitari's advice on using Maya when worthwhile. Source review,
corrective regression, clean package and independent audit are complete.
Completion documentation follows the package source in a separate commit.
Read DEC-20260927-01 through DEC-20260927-03 before further work.

**Previous completed bounded milestone (2026-09-25): [EDIT-01](EDITING_FOUNDATIONS.md).** Andrei requested
public Massassi forum research, implementation of the recommendations and a new
goal. This explicitly resumes SED development after the completed ENGINE-01
pause. The phased goal covers robustness, headless texture alignment,
source-aware diagnostics, typed COG bindings and supported ARCH occlusion,
followed by independent review, a verified Windows package and source delivery.
Read the phase acceptance document and current WORK_LOG before continuing.
Publication constraint: keep scraped Discord logs and copied forum posts out of
public GitHub and forum posts. Raw captures stay ignored/local; use original findings and links.

**Verified implementation and Windows handoff:** implementation/package commit
`8d047860c576021ea3da22743b97f5e27e15c9cc`, version `0.4.0-mots.12-dev`.
Integrated source passes **1,329 unit tests and 126 actual headless UI checks**;
the clean Release package independently passes all 1,329 units again, without
build warnings/errors. It includes KEY/3DO preservation fixes, strict neighbor UV alignment,
containment/capacity/direct-asset/world-bake diagnostics, typed COG discovery and
guarded GUI/agent rebinding, and the separate world-occluded ARCH recipe.
Independent review produced executed corrections for resource revision scope,
GUI script ambiguity/lifetime, and a thin-polygon ray predicate. Raw captures
remain local. Six self-contained workflows pass **272 agent calls / 866 checks**,
plus separate smoke. Exact verification covers 693 manifested distribution
files, ZIP membership, source and complete Git history at the same commit.
An independent check also matches all 745 source ZIP members to Git blobs.
Handoff: `build/packages/SED-MotS-0.4.0-mots.12-dev-8d04786/`.
Source main was pushed and freshly verified at `8d04786`; this completion
documentation follows in a separate commit. Final publication receipts belong
under `build/edit01-20260925/publication.json`. No binary GitHub release or new
engine/retail/gameplay qualification is claimed. The three unrelated `.DS_Store`
deletions remain uncommitted. See DEC-20260925-04 for exact hashes and boundaries.

**Current direction:** the local MCP-01 adapter now exposes the headless
contracts with progressive discovery, structured results and inline previews.
A model-driven Codex design acceptance run and durable preview recovery are
useful subsequent scopes with their own acceptance criteria.
Shared unsaved GUI sessions and an ACP controller/panel are separate later
capabilities; neither exists merely because the CLI is automated.
See [the agent roadmap](AGENT_DESIGN_ROADMAP.md).

**Pre-implementation research checkpoint (2026-09-25):** source was at published
`c6aabe620a2e18a5a7a5d78aa327b6441a873c51`; no implementation or new publication
was performed at that checkpoint. The requested public-fork/add-on review and private local channel
archive are under ignored `build/research-20260925/`. Start with `README.md`,
`TOOL_INVENTORY.md`, and `discord/TECHNICAL_FINDINGS.md` there. Do not publish that
directory: it contains private channel text and attachment references.

The six-repository, sixteen-branch public SED review found three older unique
commits, not a new MotS lighting correction. The new Urgon Qt editor was not found
as an obtainable source/build in the inspected routes. Original packaged-library
fixtures reproduce a before-first-keyframe sampling exception and nonzero 3DO
insertion-offset loss; the latter is not reached by current sector export.
Prioritize those scoped robustness findings, source-aware diagnostics, shared
neighbor texture alignment, COG binding queries, and ARCH occlusion. World-light
shadow rays, independent mono/RGB data, and source-point ARCH authoring already
exist. See DEC-20260925-01; research recommendations are not implemented features.

**Previous completed bounded milestone: [ENGINE-01](ENGINE_CAPTURE.md).**
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
A fresh packaged single-case capture also passed. That ENGINE-01 milestone did not
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

**Historical ENGINE-01 stop condition:** publish that tested revision and
completion checkpoint, then pause. That publication was verified at `c6aabe6`.
Andrei resumed SED implementation on 2026-09-25 for EDIT-01 above; this does not
independently authorize unrelated engine-repository work.

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

## Objective and milestone history

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

- Current editor branch: `main`; MCP-01 implementation/package is `ce093bdeb2c568b3717a1649775af9721e0a0ddc`. Completion documentation follows separately; final publication receipts are local under `build/mcp01-20260927/`. Previous EDIT-01 implementation/package is `8d047860c576021ea3da22743b97f5e27e15c9cc`.
- Historical ENGINE-01 implementation/package is `ea4d6f4a8c39501198064a52264317b16e5741e0`. Previous ARCH-01 implementation/package is `adb0caeab1f6a4f4f089cd1df94bd7dcc2ce9441`.
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
| [Thin MCP adapter (MCP-01)](MCP_INTEGRATION.md) | Shared commands, typed discovery/errors/images and explicit identities; compare against CLI on the same task | Complete within the documented isolated-file and preview scope; model-driven Codex acceptance and durable preview outcomes remain separate |

## Existing evidence and boundaries

The previous Windows package is `0.4.0-mots.6-dev` at editor base `9d1d316`. Its historical evidence includes 801 unit tests, 111 packaged lit/resource assertions across 34 CLI calls, and 39 stock MotS levels / 2,353 ARCH bindings. The separate engine harness passed 1,185 checks. These counts belong to the previous checkpoint; the LIGHT-01 results above identify the new evidence. The stock/community corpus was not rerun for this milestone.

One nominally matched Rebel Base editor/OpenJK image pair measured 1.211/255 mean absolute RGB difference and 97.59% of pixels within eight values in every channel. This is one static preview versus one timed OpenJK frame, not retail or gameplay parity. Hierarchy pivots, lower-LOD drawing, complete sky/depth/material behavior and deterministic simulation remain separate gaps.

Keep original stock/community corpus inputs separate from WBW conversions. Keep game assets and private consultation transcripts out of source distributions. The local consultation synthesis and sent briefing are in `build/lily-consultation-20260912/`; the work log records technical decisions without depending on access to that conversation.

The historical ENGINE-01 Windows/source handoff is `build/packages/SED-MotS-0.4.0-mots.11-dev-ea4d6f4/`; its `verification.json`, five workflow receipts and `SHA256SUMS.txt` describe that milestone. Package source, binaries and complete history identify `ea4d6f4`. See `DEC-20260920-19` for its hashes and counts; current MCP source/package status is at the top of this file. The ENGINE-01 package's `agent-lit-snapshot/architecture-baked.jkl` and `architecture-stale.jkl` demonstrate authoring/readback; `agent-spatial/relay-final.jkl` remains the original eight-room structural fixture. Prior packages remain separate, including the superseded `.11-dev-ed17c14` capture-tool candidate. That milestone's runtime evidence is in `build/engine01-20260920/matrix-02/`, packaged fixture preparation in `fixture-07/`, and packaged diagnostic in `diagnostic-08/`. Stock bootstrap assets and the separate engine are not included in the editor package. This is bounded static engine evidence, not retail parity or a playable-level claim.

## Recovery discipline

Check `git status` and active agent ownership before edits. Read the latest work-log entries and linked receipts. Distinguish proposed, implemented, tested and packaged states. If a prior assertion lacks evidence, retain it as unresolved rather than reconstructing a result. Update this checkpoint at meaningful transitions and append corrections to the work log instead of silently rewriting earlier findings.
