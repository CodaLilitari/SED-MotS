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
including surface-lighting and material edits. It also checks retained geometry
text against its captured hash and applies the shared section policy. Valid
comments on section headers remain supported; extra header tokens refuse
regeneration. Unchanged malformed/opaque geometry may remain readable and be
retained during unrelated edits. This is not an automatic repair facility.

Existing material slots survive duplicate or case-insensitive matching names
when the surface's selected slot still matches. Inferred material registrations
commit only after successful output generation and preservation checks. This
does not promise rollback of a successful Build if a subsequent filesystem
write fails, nor a transaction over every mutable derived model cache.

The contract does not cover arbitrary COG argument typing/remapping, thing or
UV reference identities, persistent editor IDs, or every resource table.
Original adjoin slots, flags and reciprocal partners are protected, but the
writer still emits the legacy distance field as `0.00`; byte-for-byte adjoin
metadata preservation is not claimed. Native/model-created documents without
parsed source identities continue through the model-generated export path.

## Acceptance evidence

Current state: implementation and integrated source/executable checks passed;
new self-contained package verification remains pending.

| Evidence | Result |
|---|---|
| Full Release solution build | Passed, zero warnings/errors |
| Full unit suite | 952 passed, zero failed/skipped; includes 26 export-reference and 18 agent-geometry cases |
| Actual Avalonia headless controls | 71 checks passed; no native GUI window opened |
| Enhanced framework-dependent agent design workflow | 69 process calls, 129 assertions passed, including source-light state, exact room bounds/counts/neighbors and negative controls |
| Diagnostic images | Both interior PNGs inspected: distinct views show continuous room geometry and open connections; flat diagnostic colors, no material/gameplay claim |
| New self-contained Windows distribution | Pending |

Source build/TRX evidence: `build/dotnet/Release-5a3b21746918409f908e20492e52ac4c/`.
The UI log is `build/geometry-ui-probe.log`. Process requests, responses, hashes
and images are in `build/geometry-preservation-20260920/design-final/`.
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
