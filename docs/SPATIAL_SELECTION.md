# Spatial selection and authoring (SPATIAL-01)

Status: complete within the documented scope, packaged and source published.
Implementation/package commit: `1fe296a65f331161e13a4dac20f3306d322d3b53`.
This milestone follows [GEO-01](GEOMETRY_REFERENCE_PRESERVATION.md); its results
are separate from earlier package counts. Completion documentation is a later
commit than the packaged source.

## Purpose and scope

An agent should describe the room and face it intends to edit, inspect the
match, and submit that exact selection without calculating serialized geometry
indices. Selection is shared Core geometry behavior, exposed through the same
headless CLI that uses the human editor's commands. A new GUI selection panel,
general reference remapping and an MCP transport are outside this milestone.

The acceptance task is an original eight-sector Relay Outpost: a six-room loop,
a security annex and an upper observation room. It must be built and revised
through spatial targets, saved/reopened, checked structurally and rendered
offscreen. This is an architectural editing fixture, not a playable mission or
an engine/retail compatibility claim.

## Selection contract

`sed-agent select <level> <selection.json|->` is read-only. Discover the current
contract with `schema select`. Requests use `schemaVersion: 1`, `kind` equal to
`sector` or `surface`, and a world-space `sectorPoint`. Surface requests also
require an `outward` direction. The default distance tolerance is `1e-7` world
units, bounded to `1e-9..1e-3`; default angular tolerance is `0.1` degrees,
bounded to `0..5`. Direction vectors are normalized; zero and nonfinite inputs
refuse. Optional `surfacePoint` restricts matches to faces containing that point
within the stated plane/edge tolerance.

For example, select the east wall of the room containing the origin:

```json
{
  "schemaVersion": 1,
  "kind": "surface",
  "sectorPoint": [0, 0, 0],
  "outward": [1, 0, 0]
}
```

The result reports `status` (`unique`, `none` or `ambiguous`), `stage` (`sector`
or `surface`), the full match count `total`, bounded `candidates`, and a
`selection` token only for a unique match. `limit` bounds returned candidates,
not the counting used to determine uniqueness. No match and ambiguity are
successful read-only results with `selection: null`; callers must check status.
An ambiguous containing sector is reported before surface filtering.

Containment includes boundaries, so a point on a portal can match both adjacent
rooms. Move the query point into the intended room instead of choosing the
first candidate. The bounded geometry contract requires finite, closed,
manifold, convex sectors with planar, convex, inward-facing polygons. Geometry
that cannot be safely classified produces a diagnostic; a bounding-box match
alone is not containment. Selection does not mutate cached normals or geometry.

## Bound targets and editing

Copy the returned token into an operation's `target`. It contains version,
kind, source revision, concrete sector/surface address and a geometry digest.
The token is a consistency guard, not authentication or a persistent editor ID.
It binds the full containing sector's ordered vertex positions, surface
boundaries and adjoin endpoints, as well as the selected kind/address. Cached
normals, lighting, materials, UVs and other non-geometric metadata are excluded.

Supported sector operations are `setSectorFlags`, `setSectorAmbient` and
`setSectorColormap`. Supported surface operations are `setSurfaceMaterial`,
`setSurfaceFlags`, `fitSurfaceTexture`, `setSurfaceUv`, `setSurfaceRenderModes`,
`extrudeSurface` and `adjoinSurfaces`. Adjoining uses `target` and `otherTarget`.
Existing numeric addressing remains supported; mixing the two addressing forms
for the same target refuses. Thing and vertex operations retain their existing
numeric protocol in this milestone.

The normal patch `inputSha256` and native `sourceRevision` checks still apply.
Before each operation, the selected token must match the captured source
revision and the current containing-sector geometry. The resolved operation is
then stored with concrete addresses for execution and final save/reopen
readback; spatial predicates are never silently rerun after the edits.

Metadata operations may reuse an unchanged geometry token within one batch.
An extrusion, adjoin or other change to that sector invalidates the old token
for subsequent operations. Save/reopen and select again to address new or changed
geometry. A new source file revision also requires new tokens, even for a
metadata-only change. Unrelated geometry appended elsewhere does not by itself
invalidate an unchanged sector's token inside the same patch.

Malformed or mixed addressing uses the existing invalid-request result (exit
2). Stale revision or geometry uses conflict (exit 4), with the operation and
target location identified where applicable. Planning remains read-only; apply
requires a new output and can bind the reviewed output hash. A refused batch
never writes a partial output or changes its input.

## Acceptance gates

1. Original Core tests establish containment, inward/outward direction,
   boundary ambiguity, point-on-face checks, finite/tolerance validation,
   unsupported geometry refusal and non-mutating reads.
2. Token/protocol tests establish exact schema handling, full-source/native
   dependency binding, geometry staleness within a batch, address/digest
   consistency, metadata reuse and frozen final readback.
3. The executable builds the eight-room outpost using tokens without encoded or
   derived geometry indices, closes a loop and revisits an older room to add a
   shortcut. It revises floor material/UV and ambient metadata, retaining
   independent source-light fields.
4. Independent geometric witnesses verify all room bounds/vertices, inward
   faces, nine paired open portals and the exact final adjacency graph.
   Negative controls cover no match, portal-boundary ambiguity, stale revision,
   stale geometry after an earlier batch operation, overlap refusal and unchanged
   input/no output after refusal.
5. Two offscreen interior views are inspected separately from structural
   assertions. Full solution/unit tests and the existing human-controls probe
   pass. A clean-source Windows package runs all five agent workflows and
   verifies its exact distribution/source/history identity.

Acceptance results, source identity and corrections belong in
[WORK_LOG.md](WORK_LOG.md) and the final checkpoint. Native project writing,
runtime/retail testing, complete playable mission authoring and the external
stock/community corpus remain separate milestones.

## Executed source acceptance

The full Release build passed 1,043/1,043 tests with zero warnings/errors at
`build/dotnet/Release-0c21612696854934b9eab09e60ecbdfe/`. The existing actual
Avalonia headless probe passed all 91 checks. No new native GUI was opened.

The framework-dependent executable completed the Relay Outpost workflow with
52 process calls and 458 checks at
`build/spatial01-20260920/outpost-first/receipt.json`. It produced eight sectors
and nine portal pairs, preserved the seeded source-light fields, and passed all
five negative controls. Final JKL SHA256:
`1bfae16e24c723b7621c6facaa2b2f5581c20e188d190dcd81e55203f026f7bc`.

Both diagnostic PNGs were inspected separately: the ground view shows a
continuous horizontal room connection, and the tilted downward view shows the
vertical connection from the observation room. These are flat polygon colors,
not resource-backed lighting or a playable mission. The machine receipt keeps
its original inspection flag; `build/spatial01-20260920/visual-review.md`
records the inspection and exact image hashes.

The first full run passed 1,042/1,043 because a newly authored native fixture
omitted its required HEADER section; the error occurred before the intended
companion-staleness assertion. Supplying that header let the unchanged assertion
execute and pass. The old `.8` agent's absence of `schema select` is recorded as
missing new capability, not a regression in its earlier contract.

## Verified Windows handoff

`build/packages/SED-MotS-0.4.0-mots.9-dev-1fe296a/` contains the self-contained
Windows editor/agent, corresponding source ZIP and complete Git history bundle.
The clean package build independently passed all 1,043 units at
`build/package-worktrees/spatial01-1fe296a/build/dotnet/Release-eda6c2f963264bef8cf1316b65234cd3/`.

| Packaged workflow | Agent calls | Checks |
|---|---:|---:|
| General editing | 24 | 26 |
| Source lights | 34 | 47 |
| Connected design | 69 | 129 |
| Spatial Relay Outpost | 52 | 458 |
| Lit snapshots | 34 | 111 |
| Total | 213 | 771 |

Separate smoke checks passed. `verification.json` matched all 684 distribution
files, the exact ZIP member set, source archive and history commit. The packaged
Relay Outpost JKL and both PNGs hash-identically to the accepted/inspected
framework-dependent artifacts. No installer or game was run and no native GUI
usability session or GitHub binary release is claimed.

Windows ZIP SHA256:
`7c371a9a8950d7f297cf85d8783aa2d21eca1cfddd4949900bfefa2ff06a82f3`.
Full hashes and publication evidence are recorded in `DEC-20260920-11`.
