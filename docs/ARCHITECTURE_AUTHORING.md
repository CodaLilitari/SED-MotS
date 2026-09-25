# ARCH-01: resource-bound architectural lighting authoring

This milestone adds shared human/agent operations for MotS architectural lighting.
Ordinary `ARCHLIGHTING` arrays remain engine-facing. The new `SED_ARCH_AUTHORING`
editor extension records explicit current-model acceptance and the origin of new
samples. It does not turn imported samples into historically verified bakes.

## Actions

| Action | Meaning |
| --- | --- |
| Bind existing samples | An unbound eligible thing takes a private copy of an existing, address-compatible object. Current captured model bytes are recorded; sample origin remains unknown. |
| Reset / rebind | Initialize an unbound thing, or replace its binding, with a private zeroed allocation for the current supported model. The explicit `reset` policy does not retain old samples in the new binding. |
| Bake source lights | Initialize or replace the selected thing's samples using explicit `source-points-v1` or `source-points-world-occluded-v1`. Previously authored bindings whose model bytes changed require explicit reset/rebind first. |
| Unbind | Clear this thing's binding and provenance while retaining the old arrays. |

Every replacement appends an object and changes only the selected thing. Shared
imported arrays and old indices remain untouched. Undo restores the exact previous
binding and provenance and removes only the command's owned replacement; redo
replays captured samples. Preparation validates and calculates before mutation.
Unrelated changes outside the undo history cause guarded undo/apply refusal.

## Sampling profile and boundaries

`source-points-v1` is a new, explicit editor recipe, not a recovered retail baker.
For each supported model vertex in its transformed rest pose and each source
light, with distance `d` and primary range `r`:

```
falloff = (1 - d/r)^2, when 0 <= d < r; otherwise 0
mono += whiteIntensity * falloff
rgb  += lightColor * effectiveRgbIntensity * falloff
```

Null RGB intensity inherits white; explicit zero remains zero. The primary range
controls both channels. Stored legacy RGB range, source flags and layer do not
affect this profile. No ambient floor, normal-facing rule, world occlusion,
architectural self-shadow, animation or dynamic lights are included. Preview and
engine composition remain separate. Generated samples are finite, nonnegative
and unclamped; finite imported samples, including negative components, remain
readable and bindable.

EDIT-01 adds opt-in `source-points-world-occluded-v1`. It uses the same falloff
and independent channels, then rejects a light/sample pair when the existing
world polygon ray predicate finds a blocker. Open adjoins transmit light unless
their `BlockLight` bit is set. A source with `NoBlock` bypasses rays. Hits within
the existing endpoint tolerance and coplanar rays follow that predicate; the new
profile does not apply the world-corner baker's same-sector shortcut. It requires
finite planar nondegenerate world faces and live reciprocal adjoins.

This is an editor recipe, not an alpha-aware material or retail light transport
model. World polygons block regardless of material filename, texture alpha,
render/fullbright modes or model face materials. Ordinary rendering still uses
its own material/light-mode rules. No model or self shadows, normals, ambient,
animation or dynamic lights are added. Changing a material alone therefore does
not change these shadow samples. Original fixtures cover this boundary.

The original profile and its input digest remain unchanged. The new profile has
its own digest including ordered world geometry/adjoins, source flags and
conservative world flags; altering those inputs marks its saved bake stale.
Both the thing inspector and agent use the same service, provenance and undo
command. Each GUI bake button names its policy, and agent patches must supply
the profile explicitly.

Bake/reset support one geometry set. A model with no hierarchy has one implicit
instance of each mesh; a declared hierarchy must be structurally valid, complete,
acyclic, and map each mesh to exactly one node. Nonzero model insertion offsets
and node pivots are refused. Supported transforms use double-precision translation
and the established yaw-Z, pitch-X, roll-Y order. This restriction avoids silently
writing one sample for several different positions sharing a mesh/vertex slot.
Existing multi-LOD bindings can still be inspected, preserved and bound when the
all-LOD address checks pass; those checks do not establish bake correspondence.

Preparation refuses more than 4,096 hierarchy nodes, depth above 128, 32 million
vertex-transform steps, or 50 million vertex/light pairs. These are explicit
authoring-work limits, in addition to the existing bounded archive/parser limits.

## Provenance and status

Each authored binding records origin, model name, raw model-entry SHA256, input
SHA256 when baked, and original sample SHA256. The legacy normalized model
fingerprint remains a separate concept. A strict versioned extension binds its
records to the current architectural addresses and arrays. Unsupported versions,
duplicate or malformed records and structural checksum mismatches are refused.
Complete removal of the extension is indistinguishable from an older file and
loses this provenance; the metadata is not an authenticity signature.

Status is computed rather than set by individual editing commands:

| Status | Meaning |
| --- | --- |
| `unbound` | No architectural attachment. |
| `unknown` | Imported binding without recognized authoring provenance. |
| `bound` | Current model accepted for retained samples, whose bake origin is unknown. |
| `reset` | Explicitly initialized/reset samples; no source-light bake claim. |
| `current-inputs` | Recorded point-bake inputs and original samples still match local document values. |
| `stale` | Relevant thing pose, model name/eligibility, or source-light inputs changed. |
| `overridden` | Stored samples differ from their recorded original sample digest. |

`current-inputs` does not assert current model-resource freshness. Run resource
validation against explicit archives to compare the captured raw entry hash.
Input digests cover the inputs actually consumed by this recipe, not unrelated
world geometry, sector ambient, source flags or archive paths. Changed inputs
preserve old samples and origin. Undoing the change can restore local freshness.
Save never silently updates bake digests to make stale samples appear current.

## Agent workflow

Use `schema <operation>` and `schema query` for executable schemas. Supply
`resourceArchives` with exact archive paths and SHA256 values in the patch.
Review both `outputSha256` and `resourceRevision`; apply rechecks resources and
source identity and writes a new destination only.

```json
{"op":"bindThingArchitecture","thing":0,"lightingIndex":2}
{"op":"rebindThingArchitecture","thing":0,"policy":"reset"}
{"op":"bakeThingArchitecture","thing":0,"profile":"source-points-v1"}
```

`query things` returns per-thing status, origin and digests. `query architecture`
lists stored objects, mesh counts and their users, including retained orphan
objects. `query architecturalVertices` returns bounded pages of independent
mono/R/G/B samples:

```json
{"schemaVersion":1,"collection":"architecturalVertices","lightingIndex":2,"mesh":0,"offset":0,"limit":50}
```

These operations use the same prepared Core commands as the inspector. The file
CLI does not synchronize unsaved GUI state. No additional MCP wrapper is required
to perform this milestone's workflow; a future transport can expose the same
domain operations without duplicating their mutation or resource checks.

## Acceptance evidence

Acceptance uses original synthetic models, source lights and closed rooms. Unit
coverage includes analytic channel values, independent null/zero RGB behavior,
transform/refusal cases, sample alias protection, exact undo/redo, extension
round trips, stale state, strict queries, and source/resource transaction guards.
The actual headless inspector exercises authoring controls, visible failures and
history. The packaged lit workflow additionally plans, applies, pages samples,
compares fixed-profile mono/RGB renders and records stale readback.

Build counts, package identity and publication receipts are recorded in
`PROJECT_STATE.md` and `WORK_LOG.md` after their corresponding checks complete.
This authoring gate is separate from shadowed architectural baking, multi-LOD
correspondence, native-project writing, engine runs, retail comparisons, complete
resource closure, and playable-level qualification.
