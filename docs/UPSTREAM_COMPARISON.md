# Editor comparison and research carry-forward

Updated 2026-09-30. This is an original engineering comparison plan, not a
transcript, release announcement or claim of tested parity. Private community
captures, copied forum posts, attachments and retail assets remain outside the
repository and distribution. The corresponding private research checkpoint
retains exact evidence references and acquisition/review coverage.

## Keep the implementations separate

| Baseline | Evidence as of this checkpoint | How to use it |
|---|---|---|
| Our SED MotS .NET fork | Current inspected source `4a9eb3c08de9c76c03fd3b5a8693691f266f483b`; latest qualified local `.15-dev` package remains pinned separately in PROJECT_STATE | Shared Core/Formats behavior, GUI/CLI/MCP contracts and original acceptance fixtures are the working baseline. Documentation changes do not rebuild the package. |
| Public Delphi SED | [smlu/SED](https://github.com/smlu/SED), inspected Pascal main `c3b6c0e59f192e96dc6df2daf698fb179e63603a`; latest observed public release v0.3.0 | Compare individual fixes and behavior at exact commits. An old binary, a new branch and a new project are different evidence. |
| Announced Qt/C++ SED rewrite | February prototype history; September 28 author report describes a from-scratch rewrite approaching release and specifically includes MotS ARCH editing and baked illumination | Await an actual obtainable build/source. No such artifact was found in the checked routes. Preserve a comparison queue now; do not assume a release date, source lineage, license, API or feature completeness. |
| Mudhorn | Separate editor with its own changing implementation; the acquired September 23 build predates later reported changes | Pin each acquired build. Later proposals and author reports do not establish features in the older package or belong to the SED rewrite. |
| Companion tools and historical editors | [Plug-in audit](audit/JED_SED_PLUGIN_AUDIT_20260930.md) and [pinned catalog](audit/JED_SED_PLUGIN_CATALOG_20260930.json) | Compare useful operations and file contracts. A default branch can be documentation-only while a release tag contains implementation, as corrected for MAT16. |

The rewrite's initial port description and later from-scratch description are
retained as different dated reports. Actual source is needed to establish its
lineage. A separately discussed material/lighting utility must not be silently
merged into the rewrite's capabilities.

## Record evidence independently

Each comparison item needs a stable capability ID, project and exact version,
source reference, last check, current disposition and a next acceptance case.
Keep these facts independent: announced, media inspected, artifact acquired,
source inspected, executed, reproduced, implemented here, independently checked
and included in a particular package. They are not interchangeable completion
states. A source review does not prove runtime behavior; a downloaded archive
does not prove that its contents implement a later announcement.

Reconcile the private evidence ledger and this plan before calling something
new, missing or complete. Search-result counts describe acquisition, not the
number of useful discoveries or the extent of semantic review. Preserve
negative searches with their scope, rather than claiming universal absence.

## Comparison queue

| ID | Capability and present disposition | Original acceptance case / next action |
|---|---|---|
| CMP-01 | MotS ARCH editing and baking overlap the announced rewrite. Our current source supports both `source-points-v1` and `source-points-world-occluded-v1`. | Compare the same single-LOD rest-pose model, independent W/RGB samples, world blockers, BlockLight portals, NoBlock sources, reset/rebind, stale inputs, save/reopen and undo. Model/self shadows, material alpha, ambient, animation, dynamic lights and LOD correspondence remain separate. |
| CMP-02 | Dynamic vertex RGB readiness differs between inspected retail and current OpenMotS loader behavior. Diagnostic implementation is pending. | Inspect actual proposed serialized rows, not just project type. Unchanged GEORESOURCE may be retained verbatim; regenerated MotS rows use four values per corner. For finite 4N rows, compare final parsed float32 values: the inspected retail loader compacts when every corner has W == R == G, ignoring B; current OpenMotS retains 4N storage. Retained scalar N-row retail behavior remains unverified. Test blue-only differences, one distinguishing corner and grey/recolor cycles. Original-binary/source evidence is not an executed retail result. No automatic epsilon edit before the runtime fixture. |
| CMP-03 | Convex mesh-to-sector decomposition is an announced rewrite direction. It is not general solid subtraction. | Use original already-negative-space meshes; require successful whole-input decomposition, convex sectors, valid reciprocal adjoins, preserved UV/W/RGB and explicit reference mapping. Refuse partial destructive output and unsupported topology. |
| CMP-04 | Cleave/snapping usability and topology growth need comparison. The inspected GUI surface-cleave action uses an automatic midpoint plane. | Expose a vertex/edge/plane snap policy, selected target and proposed cuts to both humans and agents. Preview counts, slivers, adjoin changes and reference effects on off-grid geometry before applying a compound operation. |
| CMP-05 | Thing-placement brush/scatter is a useful extension beyond existing bounded arrays. | Store accepted positions or deterministic seed/domain/exclusion parameters, cap attempts/counts, check containment and separately report collision clearance. GUI strokes and agent recipes should apply the same undoable Core operation. |
| CMP-06 | Temporary asymmetric UV diagnostics are a concrete preview improvement. | Render an original orientation marker and graduations without changing material bindings or UVs. Verify the same orientation/density interpretation in GUI and agent snapshots, including mirrored/rotated and non-square textures. |
| CMP-07 | Multi-view correctness and resource-memory behavior are comparison targets, not reproduced defects in our Vulkan implementation. | Create/destroy views and switch material/model/light modes while retaining document revision and view-local state. Measure archive/cache memory; do not infer our reader loads entire archives from reports about another editor. |
| CMP-08 | Runtime preview and editor/engine synchronization have multiple proposed designs. Existing MCP serves isolated files. | Start with explicit capability/version negotiation, resource order, source revision, typed changes, acknowledgments and stale-state refusal. A shared unsaved document and engine runtime trace are separate contracts; arbitrary code execution or another generic MCP wrapper is not required. |
| CMP-09 | Save-and-Test launch profiles should be compared against historical hardcoded-executable workflows. | Exercise explicit retail/OpenMotS executable, working directory, arguments and resource identity. Report process start separately from successful level load/gameplay. |
| CMP-10 | Material recipes, reimport and cel/mip editing have actual companion-tool precedents. | Preserve source/dialect/CMP/dimensions, frame order, fill metadata, transparency and UV intent. Report conversion losses; GUI-driven batch source is not a verified unattended CLI. |
| CMP-11 | Named scopes, property protection and lighting adjustments are known historical design leads, not newly discovered requirements. | Separate frozen membership from a saved query, geometry pin from property lock, base lighting from additive adjustments, and reimport identity from array index. Apply changes through common revision-bound commands. |
| CMP-12 | COG scenarios and dependency/model validation extend existing typed binding and preflight work. | Use explicit game profiles and exact resources; separate syntax/AST checks, mocked VM scenarios and real engine execution. A future source IDE must preserve labels, source order and message fallthrough rather than split handlers into isolated code. Add resource closure, byte/encoding diagnostics, LOD/hierarchy and normal-preservation reports before lossful conversion. |

For CMP-08, the public [WDFUSE renderer integration](https://github.com/df21-net/DarkForcesRenderer/blob/bf613af9dd5915954246545b4a813bcab915df33/Assets/Dark%20Forces%20Showcase/LevelExplorer/LevelExplorer.cs)
is a concrete source precedent. Its localhost JSON handler supports full load
and partial sector/object changes, but closes the HTTP response before applying
them. Our proposed bridge should distinguish received, applied and observed
states, with an exact resulting revision or a reported failure. The related
[LevelPreview operation documentation](https://github.com/The-MAZZTer/DarkForces/blob/c7506de75895560d2a76efb9a0a397a6663ad8e0/LevelPreview.md)
is useful for operation inventory. These are Dark Forces sources, not proof of
Sith compatibility; neither integration was built or executed in this audit.

The inspected OpenMotS source at
`b1be98bd4f170b5384cb71b15443a274aac6a937`, `src/Main/sithCommand.c`, also
registers `cogtrace`, `coglist`, `cogstatus`, `aistatus` and `cogpause` under
`DEBUGFLAG_IN_EDITOR`. This is an existing source lead for bounded runtime
observations/debugging, not an implemented remote API or evidence that all
commands work in every runtime profile. Keep observational tracing separate
from deliberate state-changing debug operations and normal gameplay acceptance.

## When a new build or source becomes available

1. Acquire it from a verified observed route; retain version, source revision,
   hashes, notices and acquisition receipt. Do not replace the old comparison
   artifact or run an uninspected attachment simply because it was posted.
2. Extract a bounded feature delta from its documentation and source. Mark
   unsupported or ambiguous claims rather than guessing from language or commit
   count. Check dependencies and applicable source notices before reuse.
3. Run a small original fixture for one capability through both implementations.
   Compare semantic output: geometry, identities/references, UVs, independent
   W/RGB, resource resolution, diagnostics and undo/save behavior. Textual file
   equality and a similar screenshot each answer only part of that question.
4. Adopt a scoped fix or original implementation only when the behavior is useful
   and understood. Keep the operation in shared Core/Formats with GUI and the
   existing agent interfaces; add a new transport only for a demonstrated missing
   boundary such as engine observations or shared live documents.
5. Record the exact implemented/tested/package states and unresolved limits.
   Compare editor preview, OpenMotS execution and retail separately. Do not let a
   future C++ release displace current work solely because of its language.

This checkpoint adds research and planning, not a new implementation milestone,
package, external reviewer acceptance or runtime qualification.
