---
name: sed-mots
description: Inspect, design, edit, convert and validate Jedi Knight Dark Forces II and Mysteries of the Sith levels with the local SED MotS agent CLI. Use for level work and editor automation; OpenJKDF2 is the primary engine target.
---

# SED MotS level work

Use the local `sed-agent` executable for supported level operations. It shares the editor's model and Core commands and can work without opening a window. Follow the user's chosen game, project and authorized scope.

## Discover the installed tools

If the installed MCP adapter is connected, start with `sed_discover` and use its
focused schemas. It exposes the same domain requests; adapter limits may be
tighter than the CLI schema. Use `sed_plan` before `sed_apply`, preserve the exact
reviewed output hash and resource revision, and retain one `operationId` per
intended write. After an interruption query `sed_outcome`; matching bytes do not
prove a lost request committed, and an existing ID never resumes an edit.
`sed_snapshot` returns an inline static image and accepts JKL only; convert
native projects with reviewed `sed_convert` first.
Conversion commits also carry `expectedInputSha256` and `expectedSourceRevision`
from the exact conversion plan, even when normalization keeps output bytes equal.
See the distributed [MCP guide](../../../docs/MCP_INTEGRATION.md). The CLI remains a complete option
when the adapter is absent.

Resolve the intended executable from the user's installation or this repository's verified package. A Windows package places it at `agent/sed-agent.exe`. Run `--version` and `--help`; use `schema commands`, `schema query` or a focused operation schema as needed. Installed capabilities are authoritative: do not infer a command's fields from a different build or the roadmap.

Read [the protocol](../../../docs/AGENT_PROTOCOL.md) for input/output contracts and preservation limits, and [the workflows](../../../docs/AGENT_WORKFLOWS.md) for worked CLI examples. These links resolve from this skill in the source tree or complete distribution. If using a separately copied skill, locate those documents in the chosen editor installation.

Invoke the executable with an argument array. Supply JSON through stdin using `-` or an explicit request file, check the exit code and parse stdout as JSON. No model service, API key or MCP server is required.

## Design and edit a level

1. Inspect or query the explicit input to establish its game, source hash, counts and preservation warnings. For native projects, retain the reported `sourceRevision` with the project hash. Keep original sources and companion templates available.
2. Query the objects and resource candidates needed for the requested design. Use bounded pages, follow continuation offsets and use returned indices; names need not be unique. Recheck source identity when combining pages. When the executable offers `select`, discover `schema select` and choose a room by `sectorPoint` or a face by its outward direction. Accept a token only from a unique result; boundary points can be ambiguous. Copy the token into supported operations' `target`/`otherTarget`. Tokens bind the full containing-sector geometry and source revision; after a geometry edit, apply/reopen and select again. Use geometry and lighting readback rather than guessing indices.
3. Discover the relevant operations before constructing a patch. Depending on the installed build, these may include material/colormap registration, surface UV and render settings, extrusion and adjoins. Registration does not prove resource availability, and adding a box does not connect it to neighboring sectors.
4. Plan the edit on the isolated model. For MotS ARCH bindings, supply explicit hash-bound `resourceArchives` when available; discover `schema resources` for standalone validation/conversion. Inspect each target's before/after state, serialized final values, ARCH diagnostics and preservation warnings. New object indices come from the plan's receipts. A later operation can intentionally replace an earlier value.
5. Apply to a new output using the plan's reviewed output hash. When resources were validated, copy its `resourceRevision` into the patch as a separate guard; identical JKL bytes do not prove that a same-name 3DO stayed unchanged. Query and validate that output. If the input, native template revision, resources or intended request changed, produce a fresh plan rather than reusing stale selectors.
6. When the build offers `snapshot`, discover its request schema and use an explicit camera to examine the requested area. Diagnostic mode checks geometry and rejects lit-only options. Lit mode requires explicit resource archive paths and SHA256 hashes. Keep `renderProfile` fixed when comparing `lightingMode` mono versus RGB: the default `openjk` profile uses base palette color times textured scalar light; `palette` selects CMP light-table rows. Ordinary model things still use mono in an RGB world. Keep strict asset checking enabled unless the user needs a clearly labeled incomplete preview. Inspect the returned image artifact, used-resource provenance and warnings. A rendered image supplements geometric checks; it does not prove engine loading or gameplay.

Use the existing authorization for ordinary in-scope edits. Planning is a machine verification step, not a requirement to ask the user to approve every operation. Continue independent parts of the task when an unsupported operation blocks one part.

## Preserve meaning and report outcomes

For architectural lighting, discover `schema bindThingArchitecture`, `schema rebindThingArchitecture` and `schema bakeThingArchitecture`. These require explicit hash-bound resources. Bind copies retained compatible samples; reset/rebind creates zeros; `source-points-v1` is an unoccluded, non-directional source-light recipe for supported single-LOD rest-pose models, without ambient or shadow solving. Query `things` for status/provenance, `architecture` for object/mesh slots, and `architecturalVertices` with `lightingIndex`/`mesh` for bounded sample readback. A local `current-inputs` result still requires current-resource validation. Preserve the `SED_ARCH_AUTHORING` extension and distinguish fresh samples, stale inputs, manual overrides and unknown legacy origin; see [ARCH-01](../../../docs/ARCHITECTURE_AUTHORING.md).

For actual engine inspection, read [ENGINE-01](../../../docs/ENGINE_CAPTURE.md). The Python companion tools prepare an original fixture through the real agent and invoke only an explicitly supplied capture-v1 engine with an exact bootstrap manifest in a new working directory. Use the isolated single-case diagnostic before the complete differential matrix. Keep authored geometry masks independent of observed pixel differences; include RGB swap, valid zero reset, unbound fallback, white-only changes, metadata removal/interleaving and clean-process repeats. Inspect the image and acceptance receipts. Final staged hashes and archive-name collision checks are launcher evidence; capture-v1 does not observe resolved resource bytes, effective camera, loaded ARCH arrays or deterministic simulation. A passing static matrix is not retail or gameplay qualification. A normal engine executable and ordinary launch-plan flags cannot substitute for the capture build.

MotS stores separate mono/RGB corner lighting and architectural data. Preserved baked lighting is not newly calculated lighting. Unknown sections and COG values can contain references; do not approximate unsupported delete/reorder or topology changes by rewriting raw numbers. Existing malformed levels may remain readable while edits are refused; report the actual diagnostics and supported repair options.

Source lights also distinguish white intensity from a nullable RGB override. Discover `schema setLight` and query stored and effective values before editing. Omitted fields preserve state; `rgbIntensity: null` relinks RGB to white, while explicit zero or an equal value remains independent. `legacyRgbRange` is retained metadata, not a second bake falloff. Native imports save into extended JKL; keep the native source, because extension-unaware editors can drop authoring metadata. A validated metadata binding describes the current indexed rows, not preservation history or runtime compatibility.

Lit previews and resource-aware exports share all-LOD model topology and optional native architectural fingerprint checks; normalized-text fingerprints differ from raw resource hashes. Missing historical baselines remain unknown even when current topology fits. Resource-free edits report unverified ARCH state. These checks do not establish complete runtime resource closure, calculate a bake or prove retail parity. Use the protocol for normalization, revision and fallback limits.

Use `launch-plan` to inspect the intended OpenJKDF2 or retail invocation. Report file edits, structural checks, image inspection and engine/gameplay testing separately. Describe unresolved resource or reference limitations precisely. File-based agent work does not automatically synchronize an open GUI's unsaved document.
