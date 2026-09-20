# Geometry export preservation (GEO-01)

This bounded prerequisite to spatial authoring protects supported imported
geometry identities during regeneration and lets geometry edits coexist with
the source-light authoring metadata from LIGHT-01. It does not introduce
spatial selection handles, arbitrary COG remapping, or general delete/reorder
support.

## Reproduced failures

The pre-fix writer enumerated world vertices and surfaces in sector traversal
order. A JKL can instead assign contiguous global surface ranges to sectors
in a different order. Regenerating GEORESOURCE after a surface-light edit,
while retaining otherwise unchanged SECTORS text, could therefore reconnect
those sector ranges to the wrong geometry. The original synthetic regression
builds successfully and observes sector 0 reopening at `(10, 0, 0)` instead of
`(0, 0, 0)`. Unchanged numeric COG arguments alone are not proof of unchanged
referents.

Separately, the geometry section guard did not recognize the validated
`SED_SOURCE_LIGHTS` extension. Extrusion and adjoining refused supported
source-light documents. `addBox` bypassed that guard and accepted a document
with an unknown section that could contain topology references.

## Required contract

- Parsed original geometry identities, not the sector traversal order or
  mutable display numbering, define the original serialized slots.
- Geometry regeneration must preserve supported original surface, vertex and
  adjoin identities and derive sector ranges from the actual output mapping.
  New supported records append without silently retargeting existing slots.
- If the requested topology cannot fit that preservation contract, export
  must refuse with a specific diagnostic before writing an output. This is a
  conservative refusal, not a claim that reference remapping occurred.
- `addBox`, `extrudeSurface` and `adjoinSurfaces` share the unknown-section
  preflight. Validated source-light metadata is modeled; genuinely opaque
  topology sections still refuse.
- Light positions, colors, flags, layers, white intensity, nullable RGB
  override and nullable legacy RGB-range metadata survive supported geometry
  edits. Invalid or mismatched metadata must still fail parsing.
- A failed later operation must not persist an earlier light/geometry edit.
  Reviewed output hashes and no-overwrite behavior still govern file creation.

## Implemented boundaries

The document captures original sector object identities and explicit source
labels, surface owners and global slots, world-vertex slots, and adjoin rows.
Supported appended records receive new slots. Original sector labels must be
sequential; ownership, complete surface coverage, contiguous sector ranges and
reciprocal adjoin partners must be representable. Shared copies of one imported
world vertex must retain identical position bits, including signed zero.

The shared writer checks these invariants when GEORESOURCE or SECTORS changes,
including surface-lighting and material edits. Nonempty source text requires a
captured parsed baseline, and all retained source text is checked against its
captured hash before output generation. Raw source-array edits require reopening
through the parser. A geometry-specific hash supplies focused diagnostics. Valid
comments on section headers remain supported; extra header tokens refuse
regeneration. Unmodeled tail records in MATERIALS, GEORESOURCE and SECTORS also
block replacement of their containing section. Unchanged malformed/opaque geometry may remain readable and be
retained during unrelated edits. This is not an automatic repair facility.

Existing material slots survive duplicate or case-insensitive matching names
when the surface's selected slot still matches. Sector colormap selections now
retain numeric slots too; explicit agent choices and undo restore both slot and
name. The human inspector provides an exact registered-slot selector and displays
ambiguous-name refusals without changing the model or undo history. Ambiguous
unbound names refuse. Invalid imported colormap references block
regeneration, while the existing empty-table/slot-zero scaffold stays supported.

`Build` is non-mutating: it stages inferred material registrations and computes
export normals without assigning live caches. `Save` commits registrations only
after successful file replacement. Refused generation and failed file promotion
preserve material state; a clean retry produces the same output.

Invalid imported texture-vertex references on surfaces with potentially textured
materials block regeneration with source surface/corner/index diagnostics.
Material-less faces deliberately ignore UV tokens in the inspected engine
loader, so their unusual indices remain compatible rather than being called
universal game-load failures. Those ignored tokens may be canonicalized during
geometry regeneration. No asset-aware proof of a named material's texture type
is implied; assigning a material to unresolved imported UVs requires supported
source data rather than invented coordinates.

The contract does not cover arbitrary COG argument typing/remapping, thing or
UV reference identities, persistent editor IDs, or every resource table.
Original adjoin slots, flags and reciprocal partners are protected, but the
writer still emits the legacy distance field as `0.00`; byte-for-byte adjoin
metadata preservation is not claimed. Native/model-created documents without
parsed source identities continue through the model-generated export path.

## Acceptance evidence

Current gate: **complete, packaged and source published** at implementation
commit `86428871e5b30cdbe4806c86efbfa77d7b01a6b2`.
The candidate `9a1f2ce` passed its original checks. A later uploaded-source
review prompted 13 new cases, with 12 intended failures and one passing control.
The resulting fixes and additional command/import controls now pass the full
981-case suite. The colormap inspector exception was independently reproduced
before fixing it; all 91 actual headless controls checks now pass. See
`DEC-20260920-04` through `DEC-20260920-07` in the work log. The replacement
self-contained package passed all four workflows and distribution verification;
the earlier local candidate remains preserved as historical evidence.

| Evidence | Result |
|---|---|
| Full Release solution build | Passed, zero warnings/errors |
| Full unit suite | 981 passed, zero failed/skipped; includes 26 export-reference, 18 agent-geometry, 12 import-edge, seven transaction and ten colormap identity cases |
| Actual Avalonia headless controls | 91 checks passed, including 20 sector-colormap checks; no native GUI window opened |
| Self-contained agent design workflow | 69 process calls, 129 assertions passed, including source-light state, exact room bounds/counts/neighbors and negative controls |
| Diagnostic images | Both interior PNGs inspected: distinct views show continuous room geometry and open connections; flat diagnostic colors, no material/gameplay claim |
| Final local Windows handoff | `0.4.0-mots.8-dev` at `8642887`; all four workflows passed (161 calls/313 assertions), separate smoke passed, 682 distribution members verified against the exact ZIP set and matching source/history; no GitHub binary release created |

Final source build/TRX: `build/dotnet/Release-1fd7de3987aa43f5bb445566ac885ce5/`.
The UI log is `build/geometry-ui-colormap-final.log`. Initial process requests, responses, hashes
and images are in `build/geometry-preservation-20260920/design-final/`.
The final clean package and receipts are in
`build/packages/SED-MotS-0.4.0-mots.8-dev-8642887/`. Its design image hashes match
the inspected framework-dependent images exactly. `verification.json` binds
the Windows binary, source ZIP and complete Git bundle to the same source
commit. Its separate clean-worktree unit receipt is
`build/package-worktrees/geo01-8642887/build/dotnet/Release-95396ba9b0ef412bae87611c16014ee8/`.
Packaging does not launch the native GUI or a game. The superseded `9a1f2ce`
candidate and failing baselines remain available locally.
Tests include valid closed rooms with reversed global surface blocks, semantic
COG witnesses resolved from emitted tables, append/reopen/append-again,
overlap-plus-orphan ranges, adjoin flags/mirrors, stale retained source, invalid
metadata and direct-save refusal without destination/material mutation.

Before production fixes, the dedicated exporter regression failed on the
intended geometric target mismatch. The eight source-light/geometry tests
produced four intended failures and four passing controls. An initial test
compile error was corrected before the exporter regression reached its
assertion; that compile error is not counted as regression evidence.

The existing self-contained `0.4.0-mots.7-dev` agent also fails the enhanced
design workflow at its first extrusion of a source-light-bearing level,
reporting `SED_SOURCE_LIGHTS` as unmodeled. The workflow retains exact requests,
replies and source hashes. Its new light fixtures distinguish inherited RGB,
explicit zero and explicit-equal intensity, and it includes late-operation
and opaque-box refusal controls.

The first post-fix focused run passed 82/83: an existing coincident-vertex
positive still used an opaque CUSTOM section. Four existing geometry-positive
tests now use the explicitly named known-sections-only derivative, preserving
their original geometric assertions. Opaque unchanged/unrelated-edit recovery
tests remain, alongside explicit refusal negatives. The subsequent full run
passed all 952 cases.

Source fixtures are original and synthetic. No retail/engine run or stock/
community corpus refresh was performed for this milestone. These checks do not
establish a complete playable level-design workflow.
