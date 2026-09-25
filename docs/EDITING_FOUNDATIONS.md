# EDIT-01: shared editing, diagnostics and lighting foundations

Status: implemented, reviewed, Windows packaged and source published on 2026-09-25.
Implementation/package commit: `8d047860c576021ea3da22743b97f5e27e15c9cc`.
Completion documentation follows separately. Started from published `c6aabe6`.
Andrei resumed implementation after the research handoff and requested public
Massassi forum research and a new goal. This supersedes the previous pause for
this SED work. OpenJKDF2/OpenMotS remains primary; retail results stay separate.

## Delivery phases

1. Verify public Massassi forum guidance against current source and record exact
   references. Scraped Discord logs and copied forum posts stay in ignored local
   research. Andrei explicitly prohibited publishing either in public GitHub;
   publication review checks staged paths/content, with only original summaries
   and source links in public documentation.
2. Correct accepted KEY sampling before its first entry and preserve nonzero 3DO
   insertion offsets. Do not relax unsupported ARCH transform guards.
3. Expose neighbor texture alignment through shared Core/GUI/agent behavior,
   including strict validation, exact undo, source-bound selectors, plan/apply,
   save/reopen and final readback.
4. Add read-only capacity, containment, asset/dependency and bake explanations.
   Preserve ambiguous overlap and unknown resource states; do not infer engine
   limits from old forum anecdotes or silently rewrite level data.
5. Add typed COG binding discovery and narrowly controlled editing using actual
   script schemas, source/resource identities and original interaction fixtures.
6. Extend architectural lighting with explicit occlusion/material behavior and
   controlled fixtures. Preserve the existing unoccluded recipe's identity and
   do not equate a new editor bake with recovered retail semantics.
7. Independent review, relevant regression/agent/UI checks, a verified Windows
   package and source publication to `CodaLilitari/SED-MotS` main. Preserve the
   three pre-existing `.DS_Store` deletions and exclude private research/assets.

## Acceptance and evidence

- Each change has an original counterexample or useful acceptance fixture,
  executed against the current source build. Historical reports guide tests;
  they are not proof that the present implementation is defective.
- GUI and agent operations share domain commands; scripts do not manipulate GUI
  controls to supply missing authoring behavior. Discovery schemas describe the
  exact supported contract, including rejected inputs and preservation limits.
- Plans leave inputs unchanged. Applied output is reopened and compared with
  planned output; stale documents/resources and unsafe reference changes fail.
- Per-phase results, review findings and unresolved limitations are recorded in
  WORK_LOG.md and the short checkpoint. Mark the overall goal complete only when
  required phases and final delivery checks are complete.
- No new API billing or blanket third-party plugin execution. Reviewers receive
  a bounded authorized source packet, never the private channel archive.

## Agent contracts

Discover the installed build using `--version` and focused `schema` commands.
The CLI remains the execution layer for Codex and other agents; these features
need neither screen control nor an additional MCP server. A later MCP wrapper
can expose the same bounded requests without implementing another editor model.

`alignSurfaceTexture` takes `sector`, `surface`, `otherSector`, `otherSurface`;
the other surface supplies the reference UV mapping. Paired `target` and
`otherTarget` selection tokens are also supported. Preparation refuses ambiguous
or multiple shared edges, degenerate/nonplanar faces, non-affine reference UVs
and output beyond one million texels. Exact endpoint identity takes precedence
over tolerance matching. Reference UVs, geometry, materials and lighting remain
unchanged. The shared command calculates the full result before mutation.

`query` collection `thingContainment` pages things using their original indices
and supports name/template search. It reports status, assignment, candidate
sectors and boundary flags. Overlap may be legitimate; unsupported geometry has
unavailable results rather than guessed reassignment. This is point containment,
not collision-volume or gameplay simulation. Each selected thing scans geometry.
The GUI consistency window runs it only when explicitly enabled. Retail capacity
warnings compare serialized capacity with the existing inferred range; they do
not clamp values or apply retail assumptions to OpenJK profiles.

`lighting` accepts a minimal request:

```json
{"schemaVersion":1,"sector":0,"offset":0,"limit":20,"castShadows":true,"updateSectorAmbients":true}
```

Omit `sector` to consider all world surfaces. It reports the actual editor
baker's facing/range/shadow decisions and isolated mono/color contribution counts
for each selected source light. Counts are surface corners, not distinct
vertices. It neither bakes nor predicts aggregate ambient, accumulated output,
ARCH, game lighting or retail behavior. Invalid geometry/source data remain
explicit; unavailable counts are null. A 50-million corner/light-pair budget
bounds one diagnostic call. A selected page builds one shared shadow grid.

`bindings` first needs a placed index from `query` collection `cogs`, and exact
archive paths/hashes supplied by the caller:

```json
{
  "schemaVersion":1,
  "inputSha256":"SOURCE_HASH",
  "resourceArchives":[{"path":"C:/Projects/example/resources.goo","sha256":"ARCHIVE_HASH"}],
  "cog":0,"offset":0,"limit":20
}
```

Native projects also require `sourceRevision`. The response includes
`scriptSha256`, schema completeness, exact parameter count, original parameter
indices and raw values, modifiers, resolved target addresses and event-link
candidates. Missing/incomplete schemas do not type raw integers. `nolink`, link
IDs and masks are retained; possible event links are not runtime subscription
or gameplay proof. Surface addresses use imported global ordering, then expose
sector/local coordinates.

A reviewed row can become an operation in the ordinary patch protocol:

```json
{"op":"rebindCogReference","cog":0,"parameter":0,"symbol":"trigger","kind":"sector","targetIndex":1,"scriptSha256":"SCRIPT_HASH"}
```

The patch requires explicit `resourceArchives`. Supported kinds are `thing`,
`sector`, global `surface` and placed `cog`; `-1` explicitly authors null.
Rebinding requires the exact complete schema and existing parameter count, and
does not pad, discard, reorder or execute scripts. Static-world/runtime COG
targets are not guessed. GUI typed edits use the same service; explicit raw
positional editing remains available for unresolved layouts.

Revision scopes are deliberately distinct. `bindingResourceRevision` can recheck
the same binding query. `assetResourceRevision` can recheck the same asset scan
and budgets. Neither is the patch's `resourceRevision`: a patch may additionally
read other scripts and every bound ARCH model. Build the patch using script and
archive hashes, review `plan`, then copy **that plan's** `resourceRevision` and
`outputSha256` for exact replay. Inputs and every captured resource folder/name
are rechecked immediately before creating a new output.

`assets` uses the same source/hash/archive request, without `cog`, plus optional
`offset`, `limit`, `maxRows` and `maxWork`. It resolves known direct references
from level material/colormap tables, sectors, surfaces, placed COGs and supported
template/thing fields. Each row identifies its origin and captured bytes or an
explicit missing/unknown/null result. Defaults cap the scan at 10,000 rows and
100,000 work units; maxima are 100,000 and one million. Incomplete scans say so.
Pages repeat the bounded scan; `nextOffset` pages only the inspected rows.
Transitive resources, COG parameters/code, dynamic resource names and runtime
loader behavior remain unverified. A resolved entry is an existence/hash receipt,
not proof that its contents are format-valid or render correctly.

The new `source-points-world-occluded-v1` ARCH profile is described in
[ARCHITECTURE_AUTHORING.md](ARCHITECTURE_AUTHORING.md). It shares the inspector,
agent, resource capture, provenance and undo path, preserving the original
unoccluded profile. It does not claim material-alpha or model/self shadows.

## Delivery status

Implementation and independently verified review corrections are complete.
Final source validation passed 1,329 tests and 126 actual headless UI checks.
A clean detached-worktree Windows build independently passed all 1,329 units
again, with zero build warnings/errors. The self-contained `0.4.0-mots.12-dev`
package passed all six workflows: 272 actual agent calls and 866 checks, plus
separate smoke. Exact distribution verification matched 693 manifested files,
ZIP membership, the source archive and complete Git history at the same commit.
An independent audit additionally matched all 745 source ZIP members to Git blobs.

The implementation is published on `CodaLilitari/SED-MotS` main. Local package:
`build/packages/SED-MotS-0.4.0-mots.12-dev-8d04786/`. Exact artifact hashes,
review scope, publication receipt and limits are recorded in WORK_LOG.md
DEC-20260925-04. No new engine run, retail parity or gameplay result is claimed.
