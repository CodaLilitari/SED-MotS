# TOOL-01 — shared editing workbench

The first slice recreates useful plug-in workflows as original shared commands
and strict, discoverable agent contracts. It adds exact query filters,
multi-surface UV projection/transforms and deterministic arrays of **new** things
or lights. It does not load historical DLLs, clone reference-bearing fragments,
generate stairs/cutscenes, or change the lighting renderer. Those remain later
scopes in the [plug-in audit](audit/JED_SED_PLUGIN_AUDIT_20260930.md).

The first slice passed source, headless UI and real CLI acceptance on 2026-09-30.
Use the installed
`sed-agent --version` and focused schemas before assuming an older build supports
these operations. The existing `sed_discover`, `sed_query`, `sed_plan`,
`sed_apply` and `sed_outcome` MCP tools expose the same domain behavior; another
MCP server is unnecessary. File-based agent edits do not synchronize an unsaved
GUI document.

## Exact query filters

Optional `filter` predicates combine with AND and with existing substring
`search`. Exact text equality is ordinal case-insensitive; whitespace remains
significant. Results retain original collection indices and source revisions;
offset/limit paginate matching rows, not the original unfiltered sequence.

| Collection | Exact filter fields |
|---|---|
| surfaces (one explicit sector) | materialIndex, owning-sector layer, surfFlags, faceFlags, adjoinFlags |
| things / thingContainment | name, template, layer |
| sectors / lights | layer, flags |

Each flag predicate contains one or more unsigned 32-bit masks: `any`, `all`,
`none`. `any:0` matches nothing; `all:0` and `none:0` are vacuous. Different mask
predicates AND together. `adjoinFlags` requires an actual adjoin and reads this
surface side's flags; absent adjoins do not match even a zero mask. Material
index `-1` means untextured; a nonnegative index must exist in the material
table, and duplicate material names do not collapse distinct slots. A layer
filter matches the stored value even if an imported layer table is sparse.

```json
{"schemaVersion":1,"collection":"surfaces","sector":0,
 "filter":{"materialIndex":2,"surfFlags":{"all":4,"none":1536}},"limit":50}
```

Empty filters/masks, unknown or duplicate properties, wrong types and
collection-inapplicable predicates refuse. Filter results are not persistent
selection tokens. Re-query after source or topology changes.

## Shared UV operations

Windows: select surfaces and open **Texturing > Selection UV Workbench**.
The modal window captures that selection. Each Apply is one shared undo step.
The two agent patch operations address an explicit ordered, unique
`surfaces:[{"sector":0,"surface":1}, ...]` list:

- `projectSurfaceUv`: `direction`, `origin:[x,y,z]`, `texelsPerUnit`.
- `transformSurfaceUv`: `pivot:[u,v]`, `scale:[u,v]`, `rotationDegrees`,
  `offset:[u,v]`.

Projection uses one world origin and a fixed texel density across all selected
faces; it does not independently stretch each face to its texture dimensions.
These six bases have U cross V equal to the chosen direction:

| direction | U | V |
|---|---|---|
| +x | +Y | +Z |
| -x | -Y | +Z |
| +y | -X | +Z |
| -y | +X | +Z |
| +z | +X | +Y |
| -z | -X | +Y |

Transform order is scale about the shared UV pivot, rotate counterclockwise in
mathematical UV coordinates about that pivot, then offset. Negative nonzero
scales mirror; zero scales refuse. Equal projected world positions receive
equal UVs; transforms preserve an existing common UV seam without welding
unrelated UVs. Inputs/outputs are finite; agent coordinates and resulting UVs
are bounded to +/-1,000,000. Each agent operation supports up to 256 faces and
the whole patch up to 4096 addressed corners, counting repeated edits.

Commands preflight all targets, geometry and output before mutation, preserve
exact prior UVs for undo, and reject stale corner/vertex/UV state. Edge-on,
degenerate, nonplanar, self-intersecting or numerically collapsed projections
refuse. Materials, topology and independent white/RGB lighting remain unchanged.

## New thing and light arrays

Windows: **Tools > Place Thing / Light Array**. Both GUI and agent paths use
the shared Core commands. Patch operations are:

- `addThingArray`: existing `template`, `namePrefix`, `layout`; optional
  explicit `sector`, fixed `orientation:[pitch,yaw,roll]`, `layer`.
- `addLightArray`: `layout`, `rgb`, white `intensity`, `range`; optional
  `rgbIntensity`, `legacyRgbRange`, `flags`, `layer` as for `addLight`.

Layouts are deterministic:

| kind | fields | order |
|---|---|---|
| linear | origin, step, count | origin + i * step |
| grid | origin, stepU, stepV, countU, countV | U varies fastest |
| radial | origin, axis, radius, count, startDegrees, sweepDegrees | start + sweep * i / count; end excluded |

Radial U is the least-aligned cardinal axis projected perpendicular to the
normalized axis; V is axis cross U. Orientation stays fixed: a radial layout
does not implicitly turn things toward its center. Zero sweep/degenerate
layouts refuse; the focused schema specifies bounds. Each array has at most
256 outputs and a whole patch at most 1024 array outputs. Positions stay within
the coordinate bound. New thing names are `namePrefix_000` through `_255`;
case-insensitive collisions refuse.

All new thing points must lie strictly inside one unambiguous sector, or inside
the explicitly supplied sector for deliberate overlapping geometry. Boundary
points refuse. Point containment is not a model-radius, collision-clearance or
gameplay test. Existing declared thing capacity is respected and never silently
grown; free capacity can still be needed for runtime spawning.

New things do not copy an existing object's COG values or ARCH binding. Where
placed COGs exist, their external symbols require complete matching schemas and
exact parameter counts. Typed thing references must be canonical null `-1` or
refer to an already placed thing. Unknown or future/runtime indices refuse,
because appending could silently capture them or change runtime allocation.
Agent requests supply these scripts through explicit hash-bound
`resourceArchives`; the GUI uses its open script library. The guard revalidates
before application. It does not analyze arbitrary script code, local constants,
dynamically calculated indices or template-attached scripts.

Light arrays preserve null RGB inheritance, independent equal/zero RGB
intensity and legacy range metadata. They place source lights without baking.
Selection-wide material/model-aware lighting remains a separate milestone.

## Acceptance

Original Core fixtures check bases, seams, transform order, layout positions,
containment/capacity, independent channels, no partial mutation, stale-state
refusal and exact undo/redo. Agent tests check strict schemas, budget limits,
plan/apply/reopen and final effects after later edits. GUI probes exercise actual
controls headlessly. MCP tests use the existing reviewed commit/outcome flow.
Run the original process-level workflow against a selected installation:

```powershell
python -B tools/test_tool_workbench.py --agent C:/SED/agent/sed-agent.exe --output-dir C:/JKProjects/workbench-check-01
```

Choose a new output directory. The script records requests, stdout/stderr,
analytical readback checks, source hashes and runtime identities. It needs no
editor window, game data, graphics device or network access. Package creation
runs it against the actual self-contained agent before freezing the archive.
Executed counts and exact local package identities are recorded in PROJECT_STATE
and WORK_LOG. These are authoring and serialization results, not gameplay or
retail parity.
